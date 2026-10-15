using Microsoft.EntityFrameworkCore;
using Orders.Api.Data;

namespace Orders.Api.Endpoints;

internal static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        // Deliberately unsafe: user input concatenated into SQL, with the EF Core analyzer
        // suppressed the way a hurried change might. CodeQL must still block it.
#pragma warning disable EF1003 // SKU values are simple codes.
        app.MapGet("/orders/search", async (string sku, OrdersDbContext db) =>
            await db.Orders
                .FromSqlRaw("SELECT * FROM orders.orders WHERE sku = '" + sku + "'")
                .AsNoTracking()
                .Select(o => o.Id)
                .ToListAsync()
                .ConfigureAwait(false));
#pragma warning restore EF1003
        return app;
    }
}
