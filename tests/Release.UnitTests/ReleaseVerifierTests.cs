using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Release.Core.Deployments;
using Release.Core.Verification;

namespace Release.UnitTests;

public class ReleaseVerifierTests
{
    private const string Env = "prod";
    private const string Repo = "registry.local/release-guard/app";

    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryDeploymentStore store = new();
    private readonly FakeCluster cluster = new();
    private readonly FakeSmoke smoke = new();
    private readonly ReleaseVerifier verifier;
    private readonly DeploymentGuard guard;

    public ReleaseVerifierTests()
    {
        verifier = new ReleaseVerifier(store, cluster, smoke, clock, new VerifierOptions { Environment = Env }, NullLogger<ReleaseVerifier>.Instance);
        guard = new DeploymentGuard(store, clock, new GuardOptions());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Idle_when_nothing_is_in_flight()
    {
        Assert.Equal(TickOutcome.Idle, (await verifier.TickAsync(Ct)).Outcome);
    }

    [Fact]
    public async Task Waits_until_the_artifact_is_published()
    {
        await StartAsync(1);
        var tick = await verifier.TickAsync(Ct);
        Assert.Equal(TickOutcome.Waiting, tick.Outcome);
        Assert.Contains("not published", tick.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Waits_while_flux_has_not_applied_the_expected_revision()
    {
        await StartAsync(1, artifact: 9);
        cluster.Kustomization = new KustomizationStatus(true, "ReconciliationSucceeded", null, "1.0.0@" + Digest(8), "1.0.0@" + Digest(8));
        Assert.Equal(TickOutcome.Waiting, (await verifier.TickAsync(Ct)).Outcome);
        Assert.Null(await store.GetVerificationAsync(Env, 1, Ct));
    }

    [Fact]
    public async Task Waits_while_the_rollout_is_incomplete()
    {
        await StartAsync(1, artifact: 9);
        Applied(9);
        cluster.Rollout = Rollout(1) with { Updated = 1, Available = 1 };

        Assert.Equal(TickOutcome.Waiting, (await verifier.TickAsync(Ct)).Outcome);
        Assert.Equal(0, smoke.Runs);
    }

    [Fact]
    public async Task Passes_only_after_revision_image_rollout_and_smoke_all_check_out()
    {
        await StartAsync(1, artifact: 9);
        Applied(9);
        cluster.Rollout = Rollout(1);

        var tick = await verifier.TickAsync(Ct);

        Assert.Equal(TickOutcome.Recorded, tick.Outcome);
        Assert.Equal(VerdictResult.Pass, tick.Verdict!.Result);
        Assert.Equal("1.0.1@" + Digest(9), tick.Verdict.ObservedArtifactRevision);
        Assert.Equal($"{Repo}@{Digest(1)}", tick.Verdict.ObservedImage);
        Assert.Equal(1, smoke.Runs);
        Assert.Equal(EnvironmentState.Healthy, (await guard.ReconcileAsync(Env, Ct))!.State);
    }

    [Fact]
    public async Task Fails_when_flux_reports_the_expected_revision_unhealthy()
    {
        await StartAsync(1, artifact: 9);
        cluster.Kustomization = new KustomizationStatus(false, "HealthCheckFailed", "timeout waiting for: [Deployment/orders/orders-api status: 'InProgress']", "1.0.0@" + Digest(8), "1.0.1@" + Digest(9));

        var tick = await verifier.TickAsync(Ct);
        Assert.Equal(VerdictResult.Fail, tick.Verdict!.Result);
        Assert.Contains("HealthCheckFailed", tick.Verdict.Reason, StringComparison.Ordinal);
        Assert.Equal(EnvironmentState.Failed, (await guard.ReconcileAsync(Env, Ct))!.State);
    }

    [Fact]
    public async Task Fails_when_the_running_image_is_not_the_released_digest()
    {
        await StartAsync(1, artifact: 9);
        Applied(9);
        cluster.Rollout = Rollout(1) with { Image = $"{Repo}@{Digest(5)}" };

        var tick = await verifier.TickAsync(Ct);
        Assert.Equal(VerdictResult.Fail, tick.Verdict!.Result);
        Assert.Equal(0, smoke.Runs);
    }

    [Fact]
    public async Task Fails_when_the_rollout_exceeds_its_progress_deadline()
    {
        await StartAsync(1, artifact: 9);
        Applied(9);
        cluster.Rollout = Rollout(1) with { Available = 1, ProgressDeadlineExceeded = true };

        Assert.Equal(VerdictResult.Fail, (await verifier.TickAsync(Ct)).Verdict!.Result);
    }

    [Fact]
    public async Task Fails_when_a_smoke_step_fails()
    {
        await StartAsync(1, artifact: 9);
        Applied(9);
        cluster.Rollout = Rollout(1);
        smoke.Steps = [new SmokeStep("ready", true, "200"), new SmokeStep("create", false, "500")];

        var tick = await verifier.TickAsync(Ct);
        Assert.Equal(VerdictResult.Fail, tick.Verdict!.Result);
        Assert.Contains("'create'", tick.Verdict.Reason, StringComparison.Ordinal);
        Assert.Equal(2, tick.Verdict.Smoke.Count);
    }

    [Fact]
    public async Task Times_out_with_the_last_observation_when_no_healthy_rollout_arrives()
    {
        await StartAsync(1, artifact: 9);
        Applied(9);
        cluster.Rollout = Rollout(1) with { Ready = 0, Available = 0 };
        await verifier.TickAsync(Ct);

        clock.Advance(TimeSpan.FromMinutes(11));
        var tick = await verifier.TickAsync(Ct);

        Assert.Equal(VerdictResult.Timeout, tick.Verdict!.Result);
        Assert.Contains("0/2 available", tick.Verdict.Reason, StringComparison.Ordinal);
        Assert.Equal(EnvironmentState.VerifyTimeout, (await guard.ReconcileAsync(Env, Ct))!.State);
    }

    [Fact]
    public async Task Rules_once_per_release()
    {
        await StartAsync(1, artifact: 9);
        Applied(9);
        cluster.Rollout = Rollout(1);
        await verifier.TickAsync(Ct);

        // The lock is settled by the guard, not the verifier; until then a second tick must not rule again.
        Assert.Equal(TickOutcome.Idle, (await verifier.TickAsync(Ct)).Outcome);
        Assert.Equal(1, smoke.Runs);
    }

    [Fact]
    public async Task Verifies_rollbacks_against_the_restored_image()
    {
        await StartAsync(1, artifact: 9);
        Applied(9, seq: 1);
        cluster.Rollout = Rollout(1);
        await verifier.TickAsync(Ct);
        await guard.ReconcileAsync(Env, Ct);

        await StartAsync(2, artifact: 10);
        cluster.Kustomization = new KustomizationStatus(false, "HealthCheckFailed", "timeout", "1.0.1@" + Digest(9), "1.0.2@" + Digest(10));
        await verifier.TickAsync(Ct);

        var rollback = await guard.RollbackAsync(Env, "run", Ct);
        await store.SetArtifactDigestAsync(Env, rollback.Deployment!.Seq, Digest(11), Ct);
        Applied(11, seq: 3);
        cluster.Rollout = Rollout(1);

        var tick = await verifier.TickAsync(Ct);
        Assert.Equal(VerdictResult.Pass, tick.Verdict!.Result);
        Assert.Equal(EnvironmentState.Healthy, (await guard.ReconcileAsync(Env, Ct))!.State);
    }

    private async Task StartAsync(int n, int? artifact = null)
    {
        var request = new PromoteRequest(Env, n.ToString("x40", System.Globalization.CultureInfo.InvariantCulture), Repo, Digest(n),
            new ScanEvidence(Digest(n), "pass", new string('c', 64)), null, n, null, "run", Initialize: n == 1);
        var decision = await guard.PromoteAsync(request, Ct);
        Assert.Equal(GuardOutcome.Started, decision.Outcome);
        if (artifact is { } a)
        {
            await store.SetArtifactDigestAsync(Env, decision.Deployment!.Seq, Digest(a), Ct);
        }
    }

    private void Applied(int artifact, long seq = 1)
    {
        var revision = $"1.0.{seq}@{Digest(artifact)}";
        cluster.Kustomization = new KustomizationStatus(true, "ReconciliationSucceeded", "Applied", revision, revision);
    }

    private static RolloutStatus Rollout(int imageN) => new($"{Repo}@{Digest(imageN)}", 3, 3, 2, 2, 2, 2, false);

    private static string Digest(int n) => DeploymentGuardTests.Digest(n);

    private sealed class FakeCluster : IClusterObserver
    {
        public KustomizationStatus? Kustomization { get; set; }

        public RolloutStatus? Rollout { get; set; }

        public Task<KustomizationStatus?> GetKustomizationAsync(CancellationToken cancellationToken) => Task.FromResult(Kustomization);

        public Task<RolloutStatus?> GetRolloutAsync(CancellationToken cancellationToken) => Task.FromResult(Rollout);
    }

    private sealed class FakeSmoke : ISmokeTest
    {
        public IReadOnlyList<SmokeStep> Steps { get; set; } = [new SmokeStep("ready", true, "200"), new SmokeStep("create", true, "201")];

        public int Runs { get; private set; }

        public Task<IReadOnlyList<SmokeStep>> RunAsync(long seq, CancellationToken cancellationToken)
        {
            Runs++;
            return Task.FromResult(Steps);
        }
    }
}
