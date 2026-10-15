using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Orders.IntegrationTests.DatabaseFixture))]

namespace Orders.IntegrationTests;

/// <summary>
/// One PostgreSQL server for the whole run. Each fixture consumer gets its own database,
/// so tests that need an empty or unmigrated schema do not interfere with others.
///
/// When ORDERS_TEST_ADMIN_CONNECTION is set, that server is used instead of a container.
/// The N-1 compatibility check uses this to run an older release's tests against a
/// database migrated by the current release (ADR-0003).
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    // Same major version as the landing-zone RDS instance. Pinned by digest (ADR-0007).
    public const string Image =
        "postgres:16-alpine@sha256:721873c34ceb9f8d8fc265984940dc982404c105f19ad51be9fdc5970a6080ea";

    public const string ExternalAdminVariable = "ORDERS_TEST_ADMIN_CONNECTION";
    public const string ExternalDatabaseVariable = "ORDERS_TEST_DATABASE";

    private PostgreSqlContainer? container;

    public string AdminConnectionString { get; private set; } = string.Empty;

    /// <summary>Set when tests must run against an existing, already-migrated database.</summary>
    public string? ExternalDatabase { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable(ExternalAdminVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            AdminConnectionString = external;
            ExternalDatabase = Environment.GetEnvironmentVariable(ExternalDatabaseVariable);
            return;
        }

        container = new PostgreSqlBuilder(Image).Build();
        await container.StartAsync().ConfigureAwait(false);
        AdminConnectionString = container.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Creates an empty database and returns a connection string for it.</summary>
    public async Task<string> CreateDatabaseAsync(string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        return ConnectionFor(name);
    }

    public string ConnectionFor(string database) =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = database }.ConnectionString;
}
