using System.Net;
using System.Net.Http.Json;

namespace Orders.IntegrationTests;

public sealed class TransitionTests(DatabaseFixture database) : OrdersApiTestBase(database)
{
    [Fact]
    public async Task Happy_path_moves_through_every_state_with_fresh_etags()
    {
        var (id, etag) = await CreateAsync();

        foreach (var target in new[] { "Confirmed", "Shipped", "Delivered" })
        {
            using var response = await Client.SendAsync(TransitionRequest(id, target, etag), Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var order = await response.Content.ReadFromJsonAsync<OrderDto>(Ct);
            Assert.Equal(target, order!.Status);

            var next = response.Headers.ETag!.Tag;
            Assert.NotEqual(etag, next);
            etag = next;
        }
    }

    [Fact]
    public async Task Illegal_transition_is_409_and_changes_nothing()
    {
        var (id, etag) = await CreateAsync();

        using var response = await Client.SendAsync(TransitionRequest(id, "Delivered", etag), Ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var get = await Client.GetAsync($"/orders/{id}", Ct);
        Assert.Equal(etag, get.Headers.ETag!.Tag);
        Assert.Equal("Received", (await get.Content.ReadFromJsonAsync<OrderDto>(Ct))!.Status);
    }

    [Fact]
    public async Task Cancelling_after_shipment_is_409()
    {
        var (id, etag) = await CreateAsync();
        etag = await MoveAsync(id, "Confirmed", etag);
        etag = await MoveAsync(id, "Shipped", etag);

        using var response = await Client.SendAsync(TransitionRequest(id, "Cancelled", etag), Ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Stale_if_match_is_412()
    {
        var (id, original) = await CreateAsync();
        await MoveAsync(id, "Confirmed", original);

        using var response = await Client.SendAsync(TransitionRequest(id, "Cancelled", original), Ct);
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.NotEqual(original, response.Headers.ETag!.Tag);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("*")]
    [InlineData("W/\"1\"")]
    [InlineData("1")]
    public async Task Missing_or_unusable_if_match_is_428(string? ifMatch)
    {
        var (id, _) = await CreateAsync();
        using var response = await Client.SendAsync(TransitionRequest(id, "Confirmed", ifMatch), Ct);
        Assert.Equal((HttpStatusCode)428, response.StatusCode);
    }

    [Theory]
    [InlineData("Teleported")]
    [InlineData("1")]
    [InlineData("")]
    public async Task Unknown_target_is_400(string target)
    {
        var (id, etag) = await CreateAsync();
        using var response = await Client.SendAsync(TransitionRequest(id, target, etag), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Transition_of_unknown_order_is_404()
    {
        using var response = await Client.SendAsync(TransitionRequest(Guid.NewGuid(), "Confirmed", "\"1\""), Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Concurrent_conflicting_transitions_yield_exactly_one_success()
    {
        var (id, etag) = await CreateAsync();

        // Half try to confirm, half try to cancel, all from the same read.
        var statuses = await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            using var response = await Client.SendAsync(
                TransitionRequest(id, i % 2 == 0 ? "Confirmed" : "Cancelled", etag), Ct);
            return response.StatusCode;
        }));

        Assert.Single(statuses, s => s == HttpStatusCode.OK);
        Assert.All(statuses.Where(s => s != HttpStatusCode.OK), s => Assert.Equal(HttpStatusCode.PreconditionFailed, s));

        using var get = await Client.GetAsync($"/orders/{id}", Ct);
        var order = await get.Content.ReadFromJsonAsync<OrderDto>(Ct);
        Assert.Contains(order!.Status, new[] { "Confirmed", "Cancelled" });
    }

    private async Task<(Guid Id, string ETag)> CreateAsync()
    {
        using var response = await Client.SendAsync(CreateRequest(NewKey(), new { sku = "SKU-T", quantity = 1 }), Ct);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderDto>(Ct);
        return (order!.Id, response.Headers.ETag!.Tag);
    }

    private async Task<string> MoveAsync(Guid id, string target, string etag)
    {
        using var response = await Client.SendAsync(TransitionRequest(id, target, etag), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.ETag!.Tag;
    }
}
