using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Orders.Api.Application;
using Orders.Api.Data;
using Orders.Api.Endpoints;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);
var migrateOnly = args.Length > 0 && args[0] == "migrate";

var serviceInfo = new ServiceInfo(
    "orders-api",
    builder.Configuration["Release:Version"] ?? "dev",
    builder.Configuration["Release:Commit"] ?? "unknown");

// Structured JSON logs on stdout, with trace and span IDs on every event.
builder.Services.AddSerilog((_, logger) => logger
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", serviceInfo.Service)
    .Enrich.WithProperty("version", serviceInfo.Version)
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(sp => OrdersDataSource.Create(sp.GetRequiredService<IOptions<DatabaseOptions>>().Value));
builder.Services.AddDbContext<OrdersDbContext>((sp, options) => options
    .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), OrdersDbContextOptions.ConfigureNpgsql)
    .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<OrdersMetrics>();
builder.Services.AddScoped<OrderService>();

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();

builder.Services.AddHealthChecks()
    .AddCheck<SchemaReadyHealthCheck>("schema", tags: ["ready"]);

var telemetry = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceInfo.Service, serviceVersion: serviceInfo.Version))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
        .AddNpgsql())
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddMeter(OrdersMetrics.MeterName, "Npgsql"));

// Export only when a collector is configured, so tests and local runs need none.
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    telemetry.UseOtlpExporter();
}

var app = builder.Build();

if (migrateOnly)
{
    return await DatabaseMigrator.RunAsync(app.Services);
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) =>
    ex is not null || ctx.Response.StatusCode >= 500 ? LogEventLevel.Error
    : ctx.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Verbose
    : LogEventLevel.Information);

// Liveness has no dependencies: a database blip must not restart healthy pods.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
// Readiness requires the database and a schema at least as new as this build.
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.MapGet("/info", () => serviceInfo).WithTags("Service");
app.MapOrderEndpoints();

await app.RunAsync();
return 0;

/// <summary>Entry point marker for WebApplicationFactory.</summary>
public partial class Program;
