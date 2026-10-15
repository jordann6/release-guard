using Microsoft.EntityFrameworkCore;

namespace Orders.Api.Data;

/// <summary>
/// Applies pending migrations and exits. Run by the migration Job as the migrator
/// database user, never by the serving pods (ADR-0003, ADR-0004).
/// </summary>
internal static partial class DatabaseMigrator
{
    public static async Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<OrdersDbContext>>();

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToArray();
        LogPending(logger, pending.Length, pending);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        LogCurrent(logger);
        return 0;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying {Count} pending migrations: {Migrations}")]
    private static partial void LogPending(ILogger logger, int count, string[] migrations);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database schema is current")]
    private static partial void LogCurrent(ILogger logger);
}
