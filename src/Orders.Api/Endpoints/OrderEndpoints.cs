using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Orders.Api.Application;
using Orders.Api.Domain;

namespace Orders.Api.Endpoints;

internal static class OrderEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/orders").WithTags("Orders");
        orders.MapPost("/", CreateAsync);
        orders.MapGet("/{id:guid}", GetAsync);
        orders.MapPost("/{id:guid}/transitions", TransitionAsync);
        return app;
    }

    private static async Task<Results<Created<OrderResponse>, Ok<OrderResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        CreateOrderRequest request,
        OrderService service,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!RequestFingerprint.IsValidKey(idempotencyKey))
        {
            errors[IdempotencyKeyHeader] = [$"Header is required: 1 to {IdempotencyRecord.MaxKeyLength} visible ASCII characters."];
        }

        if (string.IsNullOrWhiteSpace(request.Sku) || request.Sku.Trim().Length > Order.MaxSkuLength)
        {
            errors[nameof(request.Sku)] = [$"Required, at most {Order.MaxSkuLength} characters."];
        }

        if (request.Quantity is < 1 or > Order.MaxQuantity)
        {
            errors[nameof(request.Quantity)] = [$"Must be between 1 and {Order.MaxQuantity}."];
        }

        if (request.CustomerReference is { Length: > Order.MaxCustomerReferenceLength })
        {
            errors[nameof(request.CustomerReference)] = [$"At most {Order.MaxCustomerReferenceLength} characters."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var result = await service.CreateAsync(
            idempotencyKey!, request.Sku!, request.Quantity, request.CustomerReference, cancellationToken).ConfigureAwait(false);

        switch (result.Outcome)
        {
            case CreateOutcome.Created:
                SetETag(http, result.Order!);
                return TypedResults.Created($"/orders/{result.Order!.Id}", OrderResponse.From(result.Order));

            case CreateOutcome.Replayed:
                SetETag(http, result.Order!);
                http.Response.Headers[ReplayedHeader] = "true";
                return TypedResults.Ok(OrderResponse.From(result.Order!));

            default:
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Idempotency-Key reused",
                    detail: "This Idempotency-Key was already used with a different request body.");
        }
    }

    private static async Task<Results<Ok<OrderResponse>, NotFound>> GetAsync(
        Guid id,
        OrderService service,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var order = await service.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (order is null)
        {
            return TypedResults.NotFound();
        }

        SetETag(http, order);
        return TypedResults.Ok(OrderResponse.From(order));
    }

    private static async Task<Results<Ok<OrderResponse>, NotFound, ValidationProblem, ProblemHttpResult>> TransitionAsync(
        Guid id,
        TransitionRequest request,
        OrderService service,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!EntityTag.TryParse(http.Request.Headers[HeaderNames.IfMatch], out var expectedVersion))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status428PreconditionRequired,
                title: "If-Match required",
                detail: "Send the ETag from your last read of this order in If-Match.");
        }

        if (!TryParseStatus(request.Target, out var target))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Target)] = [$"Must be one of: {string.Join(", ", Enum.GetNames<OrderStatus>())}."],
            });
        }

        var result = await service.TransitionAsync(id, target, expectedVersion, cancellationToken).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case TransitionOutcome.Applied:
                SetETag(http, result.Order!);
                return TypedResults.Ok(OrderResponse.From(result.Order!));

            case TransitionOutcome.NotFound:
                return TypedResults.NotFound();

            case TransitionOutcome.VersionMismatch:
                if (result.Order is not null)
                {
                    SetETag(http, result.Order);
                }

                return TypedResults.Problem(
                    statusCode: StatusCodes.Status412PreconditionFailed,
                    title: "Order changed",
                    detail: "The order was modified since you read it. Read it again and retry.");

            default:
                SetETag(http, result.Order!);
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Invalid status transition",
                    detail: result.Detail);
        }
    }

    private static bool TryParseStatus(string? value, out OrderStatus status)
    {
        status = default;
        return !string.IsNullOrWhiteSpace(value)
            && !char.IsAsciiDigit(value.Trim()[0])
            && Enum.TryParse(value.Trim(), ignoreCase: true, out status)
            && Enum.IsDefined(status);
    }

    private static void SetETag(HttpContext http, Order order) =>
        http.Response.Headers.ETag = EntityTag.Format(order.Version);
}
