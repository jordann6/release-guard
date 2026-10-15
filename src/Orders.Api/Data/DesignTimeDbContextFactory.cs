using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Orders.Api.Data;

/// <summary>
/// Used only by `dotnet ef` to build the model for migrations and scripts.
/// No connection is opened.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<OrdersDbContext>
{
    public OrdersDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql("Host=localhost;Database=orders;Username=design_time", OrdersDbContextOptions.ConfigureNpgsql)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new OrdersDbContext(options);
    }
}
