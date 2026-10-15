using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Orders.Api.Data;

namespace Orders.IntegrationTests;

internal sealed class OrdersApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:ConnectionString", connectionString);
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
    }

    /// <summary>Applies migrations through the same code path the migration Job uses.</summary>
    public Task MigrateAsync() => DatabaseMigrator.RunAsync(Services);

    public async Task<T> WithDbAsync<T>(Func<OrdersDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<OrdersDbContext>()).ConfigureAwait(false);
    }
}
