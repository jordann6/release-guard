using Amazon;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using k8s;
using Release.Core.Deployments;
using Release.Core.Verification;
using Release.Verifier;

// The independent in-cluster verifier (ADR-0006). It is the only identity allowed to
// write a verdict, and it decides from what the cluster actually runs, not from what
// the pipeline published.
var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("Verifier").Get<VerifierSettings>()
    ?? throw new InvalidOperationException("Verifier configuration is missing.");
var targets = builder.Configuration.GetSection("Verifier:Targets").Get<ClusterTargets>() ?? new ClusterTargets();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAmazonDynamoDB>(_ => settings.DynamoEndpoint is { Length: > 0 } endpoint
    ? new AmazonDynamoDBClient(new BasicAWSCredentials("local", "local"), new AmazonDynamoDBConfig { ServiceURL = endpoint, AuthenticationRegion = settings.Region })
    : new AmazonDynamoDBClient(RegionEndpoint.GetBySystemName(settings.Region)));
builder.Services.AddSingleton<IDeploymentStore>(sp => new DynamoDeploymentStore(sp.GetRequiredService<IAmazonDynamoDB>(), settings.Table));
builder.Services.AddSingleton<IKubernetes>(_ => new Kubernetes(
    KubernetesClientConfiguration.IsInCluster()
        ? KubernetesClientConfiguration.InClusterConfig()
        : KubernetesClientConfiguration.BuildConfigFromConfigFile()));
builder.Services.AddSingleton(targets);
builder.Services.AddSingleton<IClusterObserver, KubernetesClusterObserver>();
builder.Services.AddHttpClient<ISmokeTest, HttpSmokeTest>(http =>
{
    http.BaseAddress = new Uri(settings.OrdersUrl);
    http.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton(new VerifierOptions { Environment = settings.Environment, Deadline = settings.Deadline });
builder.Services.AddSingleton<ReleaseVerifier>();
builder.Services.AddSingleton<VerifierHeartbeat>();
builder.Services.AddSingleton(settings);
builder.Services.AddHostedService<VerifierWorker>();

var app = builder.Build();

// Live while the loop keeps ticking; a stuck loop restarts the pod.
app.MapGet("/healthz", (VerifierHeartbeat heartbeat, TimeProvider clock) =>
    clock.GetUtcNow() - heartbeat.LastTick < settings.Interval * 6 ? Results.Ok(heartbeat.Last) : Results.StatusCode(503));

await app.RunAsync();

internal sealed class VerifierSettings
{
    public string Environment { get; set; } = "prod";

    public string Table { get; set; } = string.Empty;

    public string Region { get; set; } = "us-east-1";

    public string? DynamoEndpoint { get; set; }

    public string OrdersUrl { get; set; } = "http://orders-api.orders.svc.cluster.local";

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan Deadline { get; set; } = TimeSpan.FromMinutes(10);
}

internal sealed class VerifierHeartbeat(TimeProvider clock)
{
    public DateTimeOffset LastTick { get; private set; } = clock.GetUtcNow();

    public object Last { get; private set; } = new { outcome = "starting" };

    public void Record(TickResult result)
    {
        LastTick = clock.GetUtcNow();
        Last = new { outcome = result.Outcome.ToString(), detail = result.Detail, at = LastTick };
    }
}

internal sealed partial class VerifierWorker(
    ReleaseVerifier verifier,
    VerifierHeartbeat heartbeat,
    VerifierSettings settings,
    TimeProvider clock,
    ILogger<VerifierWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, settings.Environment, settings.Table);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                heartbeat.Record(await verifier.TickAsync(stoppingToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed observation is not a verdict. Keep trying; the deadline still applies.
                LogTickFailed(logger, ex);
            }

            await Task.Delay(settings.Interval, clock, stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Verifier started for {Environment} using table {Table}")]
    private static partial void LogStarted(ILogger logger, string environment, string table);

    [LoggerMessage(Level = LogLevel.Error, Message = "Verifier tick failed")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}
