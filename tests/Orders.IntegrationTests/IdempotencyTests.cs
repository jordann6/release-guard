using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;

namespace Orders.IntegrationTests;

public sealed class IdempotencyTests(DatabaseFixture database) : OrdersApiTestBase(database)
{
    [Fact]
    public async Task Create_returns_201_with_location_and_etag()
    {
        using var response = await Client.SendAsync(CreateRequest(NewKey(), new { sku = "SKU-1", quantity = 2 }), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<OrderDto>(Ct);
        Assert.NotNull(order);
        Assert.Equal("Received", order.Status);
        Assert.Equal($"/orders/{order.Id}", response.Headers.Location?.OriginalString);
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag.IsWeak);
        Assert.Equal(["Confirmed", "Cancelled"], order.AllowedNextStatuses);

        using var get = await Client.GetAsync(response.Headers.Location, Ct);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(response.Headers.ETag, get.Headers.ETag);
    }

    [Fact]
    public async Task Same_key_and_body_replays_the_original_order()
    {
        var key = NewKey();
        using var first = await Client.SendAsync(CreateRequest(key, new { sku = "SKU-1", quantity = 2 }), Ct);
        using var second = await Client.SendAsync(CreateRequest(key, new { quantity = 2, sku = "  SKU-1  " }), Ct);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());

        var a = await first.Content.ReadFromJsonAsync<OrderDto>(Ct);
        var b = await second.Content.ReadFromJsonAsync<OrderDto>(Ct);
        Assert.Equal(a!.Id, b!.Id);
    }

    [Fact]
    public async Task Same_key_with_a_different_body_is_rejected()
    {
        var key = NewKey();
        using var first = await Client.SendAsync(CreateRequest(key, new { sku = "SKU-1", quantity = 2 }), Ct);
        using var second = await Client.SendAsync(CreateRequest(key, new { sku = "SKU-1", quantity = 3 }), Ct);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
    }

    [Fact]
    public async Task Concurrent_duplicates_create_exactly_one_order()
    {
        var key = NewKey();
        var sku = $"SKU-{Guid.NewGuid():N}"[..20];

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            using var response = await Client.SendAsync(CreateRequest(key, new { sku, quantity = 1 }), Ct);
            return (response.StatusCode, Order: await response.Content.ReadFromJsonAsync<OrderDto>(Ct));
        }));

        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses.Select(r => r.Order!.Id).Distinct());

        var rows = await Factory.WithDbAsync(db => db.Orders.CountAsync(o => o.Sku == sku, Ct));
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task Missing_idempotency_key_is_a_validation_error()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new { sku = "SKU-1", quantity = 1 }),
        };
        using var response = await Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Idempotency-Key", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("SKU-1", 0)]
    [InlineData("SKU-1", 1001)]
    public async Task Invalid_bodies_are_validation_errors(string sku, int quantity)
    {
        using var response = await Client.SendAsync(CreateRequest(NewKey(), new { sku, quantity }), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_order_is_404()
    {
        using var response = await Client.GetAsync($"/orders/{Guid.NewGuid()}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

public sealed record OrderDto(
    Guid Id,
    string Sku,
    int Quantity,
    string? CustomerReference,
    string Status,
    string? CancellationReason,
    string[] AllowedNextStatuses,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
