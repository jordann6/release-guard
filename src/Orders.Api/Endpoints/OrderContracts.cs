using Orders.Api.Domain;

namespace Orders.Api.Endpoints;

public sealed record CreateOrderRequest(string? Sku, int Quantity, string? CustomerReference);

public sealed record TransitionRequest(string? Target);

public sealed record OrderResponse(
    Guid Id,
    string Sku,
    int Quantity,
    string? CustomerReference,
    string Status,
    IReadOnlyList<string> AllowedNextStatuses,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static OrderResponse From(Order order) => new(
        order.Id,
        order.Sku,
        order.Quantity,
        order.CustomerReference,
        order.Status.ToString(),
        [.. Order.NextStates(order.Status).Select(s => s.ToString())],
        order.CreatedAt,
        order.UpdatedAt);
}

public sealed record ServiceInfo(string Service, string Version, string Commit);
