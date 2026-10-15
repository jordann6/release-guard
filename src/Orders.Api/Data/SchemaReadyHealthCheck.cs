using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Orders.Api.Data;

/// <summary>
/// Ready only when the database is reachable and already has every migration this
/// build knows about. A new image started against an old schema stays unready, so
/// a rolling update cannot shift traffic to it. An older image against a newer
/// schema is fine: the newer migrations are unknown to it, not pending (ADR-0003).
/// </summary>
internal sealed class SchemaReadyHealthCheck(OrdersDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
            return pending.Count == 0
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"Pending migrations: {string.Join(", ", pending)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Database unreachable.", ex);
        }
    }
}
