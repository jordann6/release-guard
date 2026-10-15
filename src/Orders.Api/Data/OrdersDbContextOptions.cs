using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace Orders.Api.Data;

internal static class OrdersDbContextOptions
{
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    /// <summary>
    /// Shared by the application and design-time tooling. The history table lives in the
    /// application schema, so the migrator needs no rights on the public schema.
    /// </summary>
    public static void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.MigrationsHistoryTable(MigrationsHistoryTable, OrdersDbContext.Schema);
}
