using Microsoft.EntityFrameworkCore;
using Npgsql;
using Orders.Api.Data;
using Orders.Api.Domain;

namespace Orders.Api.Application;

internal enum CreateOutcome
{
    Created,
    Replayed,
    KeyReusedWithDifferentRequest,
}

internal sealed record CreateOrderResult(CreateOutcome Outcome, Order? Order);

internal enum TransitionOutcome
{
    Applied,
    NotFound,
    VersionMismatch,
    InvalidTransition,
}

internal sealed record TransitionResult(TransitionOutcome Outcome, Order? Order, string? Detail = null);

internal sealed partial class OrderService(
    OrdersDbContext db,
    TimeProvider clock,
    OrdersMetrics metrics,
    ILogger<OrderService> logger)
{
    public async Task<CreateOrderResult> CreateAsync(
        string idempotencyKey,
        string sku,
        int quantity,
        string? customerReference,
        CancellationToken cancellationToken)
    {
        var fingerprint = RequestFingerprint.Compute(sku, quantity, customerReference);

        var existing = await FindKeyAsync(idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return await ReplayAsync(existing, fingerprint, cancellationToken).ConfigureAwait(false);
        }

        var now = clock.GetUtcNow();
        var order = new Order(sku, quantity, customerReference, now);
        db.Orders.Add(order);
        db.IdempotencyRecords.Add(new IdempotencyRecord(idempotencyKey, fingerprint, order.Id, now));

        try
        {
            // One transaction: the order and its key commit together or not at all.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsIdempotencyKeyViolation(ex))
        {
            // A concurrent request with the same key committed first. PostgreSQL waited
            // for it before raising the violation, so its record is visible now.
            db.ChangeTracker.Clear();
            LogConcurrentKey(logger, idempotencyKey);
            existing = await FindKeyAsync(idempotencyKey, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Idempotency key violated but no record found.", ex);
            return await ReplayAsync(existing, fingerprint, cancellationToken).ConfigureAwait(false);
        }

        metrics.Created();
        LogCreated(logger, order.Id, idempotencyKey);
        return new CreateOrderResult(CreateOutcome.Created, order);
    }

    public Task<Order?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public async Task<TransitionResult> TransitionAsync(
        Guid id,
        OrderStatus target,
        string? reason,
        uint expectedVersion,
        CancellationToken cancellationToken)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken).ConfigureAwait(false);
        if (order is null)
        {
            return new TransitionResult(TransitionOutcome.NotFound, null);
        }

        if (order.Version != expectedVersion)
        {
            metrics.ConcurrencyConflict();
            return new TransitionResult(TransitionOutcome.VersionMismatch, order);
        }

        if (!order.CanTransitionTo(target))
        {
            return new TransitionResult(
                TransitionOutcome.InvalidTransition,
                order,
                $"An order cannot move from {order.Status} to {target}.");
        }

        order.TransitionTo(target, clock.GetUtcNow(), reason);

        // The UPDATE is conditional on the version the client read, not merely the one
        // loaded above. A concurrent writer between the read and this save loses here.
        db.Entry(order).Property(o => o.Version).OriginalValue = expectedVersion;

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            metrics.ConcurrencyConflict();
            db.ChangeTracker.Clear();
            var current = await GetAsync(id, cancellationToken).ConfigureAwait(false);
            return new TransitionResult(TransitionOutcome.VersionMismatch, current);
        }

        metrics.Transitioned(target);
        LogTransitioned(logger, order.Id, target);
        return new TransitionResult(TransitionOutcome.Applied, order);
    }

    private Task<IdempotencyRecord?> FindKeyAsync(string key, CancellationToken cancellationToken) =>
        db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Key == key, cancellationToken);

    private async Task<CreateOrderResult> ReplayAsync(
        IdempotencyRecord record,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(record.RequestFingerprint, fingerprint, StringComparison.Ordinal))
        {
            metrics.KeyConflict();
            return new CreateOrderResult(CreateOutcome.KeyReusedWithDifferentRequest, null);
        }

        var order = await GetAsync(record.OrderId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Idempotency key {record.Key} points at a missing order.");
        metrics.Replayed();
        return new CreateOrderResult(CreateOutcome.Replayed, order);
    }

    private static bool IsIdempotencyKeyViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: OrdersDbContext.IdempotencyKeyConstraint,
        };

    [LoggerMessage(Level = LogLevel.Information, Message = "Created order {OrderId} for idempotency key {IdempotencyKey}")]
    private static partial void LogCreated(ILogger logger, Guid orderId, string idempotencyKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "Concurrent request for idempotency key {IdempotencyKey} lost the insert; replaying")]
    private static partial void LogConcurrentKey(ILogger logger, string idempotencyKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} moved to {Status}")]
    private static partial void LogTransitioned(ILogger logger, Guid orderId, OrderStatus status);
}
