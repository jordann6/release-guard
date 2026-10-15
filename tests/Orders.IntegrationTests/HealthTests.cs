using System.Net;
using System.Net.Http.Json;

namespace Orders.IntegrationTests;

public sealed class HealthTests(DatabaseFixture database) : OrdersApiTestBase(database)
{
    private readonly DatabaseFixture database = database;

    [Fact]
    public async Task Live_and_ready_are_healthy_on_a_migrated_database()
    {
        using var live = await Client.GetAsync("/health/live", Ct);
        using var ready = await Client.GetAsync("/health/ready", Ct);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task Ready_stays_unhealthy_until_the_schema_is_migrated()
    {
        // A new image started before its migration Job must not receive traffic.
        var connectionString = await database.CreateDatabaseAsync("unmigrated");
        await using var factory = new OrdersApiFactory(connectionString);
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live", Ct);
        using var before = await client.GetAsync("/health/ready", Ct);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, before.StatusCode);

        await factory.MigrateAsync();

        using var after = await client.GetAsync("/health/ready", Ct);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Live_stays_healthy_when_the_database_is_unreachable()
    {
        await using var factory = new OrdersApiFactory("Host=127.0.0.1;Port=1;Database=none;Username=none;Timeout=2");
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live", Ct);
        using var ready = await client.GetAsync("/health/ready", Ct);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    [Fact]
    public async Task Info_reports_service_identity()
    {
        var info = await Client.GetFromJsonAsync<Dictionary<string, string>>("/info", Ct);
        Assert.Equal("orders-api", info!["service"]);
    }
}
