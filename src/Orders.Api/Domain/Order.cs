using System.Collections.Frozen;

namespace Orders.Api.Domain;

/// <summary>
/// Order aggregate. The lifecycle is enforced here rather than in the HTTP layer,
/// so the rule holds for every caller: Received, Confirmed, Shipped, Delivered,
/// with cancellation allowed until the order ships. Delivered and Cancelled are terminal.
/// </summary>
public sealed class Order
{
    public const int MaxSkuLength = 64;
    public const int MaxCustomerReferenceLength = 128;
    public const int MaxQuantity = 1000;
    public const int MaxCancellationReasonLength = 500;

    private static readonly FrozenDictionary<OrderStatus, OrderStatus[]> AllowedTransitions =
        new Dictionary<OrderStatus, OrderStatus[]>
        {
            [OrderStatus.Received] = [OrderStatus.Confirmed, OrderStatus.Cancelled],
            [OrderStatus.Confirmed] = [OrderStatus.Shipped, OrderStatus.Cancelled],
            [OrderStatus.Shipped] = [OrderStatus.Delivered],
            [OrderStatus.Delivered] = [],
            [OrderStatus.Cancelled] = [],
        }.ToFrozenDictionary();

    // EF Core materialization constructor.
    private Order()
    {
    }

    public Order(string sku, int quantity, string? customerReference, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(sku) || sku.Length > MaxSkuLength)
        {
            throw new ArgumentException($"SKU is required and must be at most {MaxSkuLength} characters.", nameof(sku));
        }

        if (quantity is < 1 or > MaxQuantity)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), $"Quantity must be between 1 and {MaxQuantity}.");
        }

        if (customerReference is { Length: > MaxCustomerReferenceLength })
        {
            throw new ArgumentException(
                $"Customer reference must be at most {MaxCustomerReferenceLength} characters.",
                nameof(customerReference));
        }

        Id = Guid.CreateVersion7(now);
        Sku = sku.Trim();
        Quantity = quantity;
        CustomerReference = string.IsNullOrWhiteSpace(customerReference) ? null : customerReference.Trim();
        Status = OrderStatus.Received;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public int Quantity { get; private set; }

    public string? CustomerReference { get; private set; }

    public OrderStatus Status { get; private set; }

    /// <summary>
    /// Why the order was cancelled. Nullable, added by an expand-only migration, so the
    /// previous release keeps working against the new schema (ADR-0003).
    /// </summary>
    public string? CancellationReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Concurrency token, mapped to PostgreSQL's xmin system column. It changes on every
    /// update, so a write based on a stale read fails instead of overwriting.
    /// </summary>
    public uint Version { get; private set; }

    public static IReadOnlyList<OrderStatus> NextStates(OrderStatus from) =>
        AllowedTransitions.TryGetValue(from, out var targets) ? targets : [];

    public bool CanTransitionTo(OrderStatus target) => NextStates(Status).Contains(target);

    public void TransitionTo(OrderStatus target, DateTimeOffset now, string? reason = null)
    {
        if (!CanTransitionTo(target))
        {
            throw new InvalidOrderTransitionException(Status, target);
        }

        if (reason is not null && target != OrderStatus.Cancelled)
        {
            throw new ArgumentException("A reason is only recorded when cancelling.", nameof(reason));
        }

        if (reason is { Length: > MaxCancellationReasonLength })
        {
            throw new ArgumentException(
                $"Cancellation reason must be at most {MaxCancellationReasonLength} characters.", nameof(reason));
        }

        if (target == OrderStatus.Cancelled)
        {
            CancellationReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        }

        Status = target;
        UpdatedAt = now;
    }
}
