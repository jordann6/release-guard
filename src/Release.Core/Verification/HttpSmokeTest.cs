using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Release.Core.Deployments;

namespace Release.Core.Verification;

/// <summary>
/// Functional check of a freshly rolled-out release (ADR-0006): readiness, idempotent
/// creation, read-back, a valid transition with If-Match, and an invalid transition
/// that must be refused. Orders it creates are tagged so they are easy to find.
/// </summary>
public sealed class HttpSmokeTest(HttpClient http) : ISmokeTest
{
    public const string CustomerReference = "release-verifier";

    public async Task<IReadOnlyList<SmokeStep>> RunAsync(long seq, CancellationToken cancellationToken)
    {
        var steps = new List<SmokeStep>();
        try
        {
            using var ready = await http.GetAsync(new Uri("/health/ready", UriKind.Relative), cancellationToken).ConfigureAwait(false);
            if (!Step(steps, "ready", ready.StatusCode == HttpStatusCode.OK, $"{(int)ready.StatusCode}"))
            {
                return steps;
            }

            var key = $"verify-{seq}-{Guid.NewGuid():N}";
            var body = new { sku = "SMOKE-TEST", quantity = 1, customerReference = CustomerReference };

            using var created = await PostOrderAsync(key, body, cancellationToken).ConfigureAwait(false);
            var order = created.IsSuccessStatusCode ? await ReadOrderAsync(created, cancellationToken).ConfigureAwait(false) : null;
            if (!Step(steps, "create", created.StatusCode == HttpStatusCode.Created && order is not null, $"{(int)created.StatusCode}"))
            {
                return steps;
            }

            using var replay = await PostOrderAsync(key, body, cancellationToken).ConfigureAwait(false);
            var replayed = replay.IsSuccessStatusCode ? await ReadOrderAsync(replay, cancellationToken).ConfigureAwait(false) : null;
            Step(steps, "idempotent-replay",
                replay.StatusCode == HttpStatusCode.OK && replayed?.Id == order!.Id,
                $"{(int)replay.StatusCode}, same order: {replayed?.Id == order!.Id}");

            using var read = await http.GetAsync(new Uri($"/orders/{order.Id}", UriKind.Relative), cancellationToken).ConfigureAwait(false);
            var etag = read.Headers.ETag?.Tag;
            if (!Step(steps, "read", read.StatusCode == HttpStatusCode.OK && etag is not null, $"{(int)read.StatusCode}"))
            {
                return steps;
            }

            using var confirm = await TransitionAsync(order.Id, "Confirmed", etag!, cancellationToken).ConfigureAwait(false);
            if (!Step(steps, "transition", confirm.StatusCode == HttpStatusCode.OK, $"{(int)confirm.StatusCode}"))
            {
                return steps;
            }

            using var illegal = await TransitionAsync(order.Id, "Delivered", confirm.Headers.ETag!.Tag, cancellationToken).ConfigureAwait(false);
            Step(steps, "illegal-transition-refused", illegal.StatusCode == HttpStatusCode.Conflict, $"{(int)illegal.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            steps.Add(new SmokeStep("request", false, ex.Message));
        }

        return steps;
    }

    // Awaited inside the method: the request must outlive SendAsync.
    private async Task<HttpResponseMessage> PostOrderAsync(string key, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/orders", UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> TransitionAsync(Guid id, string target, string etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/orders/{id}/transitions", UriKind.Relative))
        {
            Content = JsonContent.Create(new { target }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OrderView?> ReadOrderAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<OrderView>(cancellationToken).ConfigureAwait(false);

    private static bool Step(List<SmokeStep> steps, string name, bool passed, string detail)
    {
        steps.Add(new SmokeStep(name, passed, detail));
        return passed;
    }

    private sealed record OrderView(Guid Id, string Status);
}
