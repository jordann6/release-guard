using System.Net.Http.Json;

namespace Orders.IntegrationTests;

/// <summary>
/// Gives each test class a migrated database of its own, or the externally supplied
/// database when running the N-1 compatibility check.
/// </summary>
public abstract class OrdersApiTestBase(DatabaseFixture database) : IAsyncLifetime
{
    private OrdersApiFactory? factory;

    internal OrdersApiFactory Factory => factory ?? throw new InvalidOperationException("Not initialized.");

    protected HttpClient Client { get; private set; } = null!;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var connectionString = database.ExternalDatabase is { } external
            ? database.ConnectionFor(external)
            : await database.CreateDatabaseAsync("orders").ConfigureAwait(false);

        factory = new OrdersApiFactory(connectionString);
        await factory.MigrateAsync().ConfigureAwait(false);
        Client = factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (factory is not null)
        {
            await factory.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    protected static HttpRequestMessage CreateRequest(string idempotencyKey, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    protected static HttpRequestMessage TransitionRequest(Guid id, string target, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/orders/{id}/transitions")
        {
            Content = JsonContent.Create(new { target }),
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return request;
    }

    protected static string NewKey() => $"test-{Guid.NewGuid():N}";
}
