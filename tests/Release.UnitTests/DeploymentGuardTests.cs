using Microsoft.Extensions.Time.Testing;
using Release.Core.Deployments;

namespace Release.UnitTests;

public class DeploymentGuardTests
{
    private const string Env = "prod";
    private const string Repo = "123456789012.dkr.ecr.us-east-1.amazonaws.com/release-guard/app";

    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryDeploymentStore store = new();
    private readonly DeploymentGuard guard;

    public DeploymentGuardTests()
    {
        guard = new DeploymentGuard(store, clock, new GuardOptions());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task First_release_requires_initialize()
    {
        var blocked = await guard.PromoteAsync(Request(1), Ct);
        Assert.Equal(GuardOutcome.Blocked, blocked.Outcome);
        Assert.Null(await store.GetLockAsync(Env, Ct));

        var started = await guard.PromoteAsync(Request(1, initialize: true), Ct);
        Assert.Equal(GuardOutcome.Started, started.Outcome);
        Assert.Equal(1, started.Deployment!.Seq);
        Assert.Equal(DeploymentKind.Initialize, started.Deployment.Kind);
        Assert.Equal("1.0.1", started.Deployment.ArtifactTag);

        var lockRecord = await store.GetLockAsync(Env, Ct);
        Assert.Equal(EnvironmentState.Deploying, lockRecord!.State);
        Assert.Equal(1, lockRecord.InflightSeq);
    }

    [Fact]
    public async Task Initialize_is_refused_once_the_environment_exists()
    {
        await DeployHealthyAsync(1, initialize: true);
        var again = await guard.PromoteAsync(Request(2, initialize: true), Ct);
        Assert.Equal(GuardOutcome.Blocked, again.Outcome);
    }

    [Fact]
    public async Task Pass_verdict_settles_healthy_and_records_last_healthy_release()
    {
        await guard.PromoteAsync(Request(1, initialize: true), Ct);
        await VerdictAsync(1, VerdictResult.Pass);

        var settled = await guard.ReconcileAsync(Env, Ct);
        Assert.Equal(EnvironmentState.Healthy, settled!.State);
        Assert.Equal(1, settled.LastHealthySeq);
        Assert.Null(settled.InflightSeq);
    }

    [Fact]
    public async Task Promotion_is_blocked_while_a_release_is_in_flight()
    {
        await guard.PromoteAsync(Request(1, initialize: true), Ct);
        var second = await guard.PromoteAsync(Request(2), Ct);

        Assert.Equal(GuardOutcome.Blocked, second.Outcome);
        Assert.Contains("still Deploying", second.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rerunning_the_same_release_resumes_instead_of_publishing_again()
    {
        await guard.PromoteAsync(Request(1, initialize: true), Ct);
        var rerun = await guard.PromoteAsync(Request(1), Ct);

        Assert.Equal(GuardOutcome.Resumed, rerun.Outcome);
        Assert.Equal(1, rerun.Deployment!.Seq);
        Assert.Equal(1, (await store.GetLockAsync(Env, Ct))!.LastSeq);
    }

    [Fact]
    public async Task Promoting_the_current_healthy_image_is_a_no_op()
    {
        await DeployHealthyAsync(1, initialize: true);
        var same = await guard.PromoteAsync(Request(1) with { SourceCommit = Commit(99) }, Ct);
        Assert.Equal(GuardOutcome.NoChange, same.Outcome);
    }

    [Theory]
    [InlineData("fail", true, "did not pass")]
    [InlineData("pass", false, "not the image being promoted")]
    public async Task Scan_evidence_must_pass_and_match_the_digest(string result, bool matchingDigest, string expected)
    {
        var request = Request(1, initialize: true);
        request = request with
        {
            Scan = new ScanEvidence(matchingDigest ? request.ImageDigest : Digest(77), result, new string('a', 64)),
        };

        var decision = await guard.PromoteAsync(request, Ct);
        Assert.Equal(GuardOutcome.Blocked, decision.Outcome);
        Assert.Contains(expected, decision.Reason, StringComparison.Ordinal);
        Assert.Null(await store.GetLockAsync(Env, Ct));
    }

    [Fact]
    public async Task Failed_rollout_blocks_the_next_promotion()
    {
        await DeployHealthyAsync(1, initialize: true);
        await guard.PromoteAsync(Request(2), Ct);
        await VerdictAsync(2, VerdictResult.Fail);

        var next = await guard.PromoteAsync(Request(3), Ct);
        Assert.Equal(GuardOutcome.Blocked, next.Outcome);
        Assert.Equal(EnvironmentState.Failed, (await store.GetLockAsync(Env, Ct))!.State);
    }

    [Fact]
    public async Task Missing_verdict_after_the_lease_becomes_a_timeout_and_blocks()
    {
        await DeployHealthyAsync(1, initialize: true);
        await guard.PromoteAsync(Request(2), Ct);

        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(EnvironmentState.Deploying, (await guard.ReconcileAsync(Env, Ct))!.State);

        clock.Advance(TimeSpan.FromMinutes(2));
        var settled = await guard.ReconcileAsync(Env, Ct);
        Assert.Equal(EnvironmentState.VerifyTimeout, settled!.State);
        Assert.Equal(1, settled.LastHealthySeq);

        Assert.Equal(GuardOutcome.Blocked, (await guard.PromoteAsync(Request(3), Ct)).Outcome);
    }

    [Fact]
    public async Task Rollback_republishes_the_last_verified_release_as_a_higher_release()
    {
        await DeployHealthyAsync(1, initialize: true);
        await guard.PromoteAsync(Request(2), Ct);
        await VerdictAsync(2, VerdictResult.Fail);

        var rollback = await guard.RollbackAsync(Env, "https://run/rollback", Ct);

        Assert.Equal(GuardOutcome.Started, rollback.Outcome);
        Assert.Equal(3, rollback.Deployment!.Seq);
        Assert.Equal("1.0.3", rollback.Deployment.ArtifactTag);
        Assert.Equal(DeploymentKind.Rollback, rollback.Deployment.Kind);
        Assert.Equal(1, rollback.Deployment.RestoresSeq);
        Assert.Equal(Digest(1), rollback.Deployment.ImageDigest);
        Assert.Equal(EnvironmentState.RollingBack, (await store.GetLockAsync(Env, Ct))!.State);

        await VerdictAsync(3, VerdictResult.Pass);
        var settled = await guard.ReconcileAsync(Env, Ct);
        Assert.Equal(EnvironmentState.Healthy, settled!.State);
        Assert.Equal(3, settled.LastHealthySeq);

        // Ordinary promotion works again after recovery.
        Assert.Equal(GuardOutcome.Started, (await guard.PromoteAsync(Request(4), Ct)).Outcome);
    }

    [Fact]
    public async Task Rollback_is_refused_when_nothing_is_broken()
    {
        await DeployHealthyAsync(1, initialize: true);
        Assert.Equal(GuardOutcome.Blocked, (await guard.RollbackAsync(Env, "run", Ct)).Outcome);
    }

    [Fact]
    public async Task Rerunning_a_rollback_resumes_it()
    {
        await DeployHealthyAsync(1, initialize: true);
        await guard.PromoteAsync(Request(2), Ct);
        await VerdictAsync(2, VerdictResult.Fail);
        await guard.RollbackAsync(Env, "run", Ct);

        var rerun = await guard.RollbackAsync(Env, "run", Ct);
        Assert.Equal(GuardOutcome.Resumed, rerun.Outcome);
        Assert.Equal(3, rerun.Deployment!.Seq);
    }

    [Fact]
    public async Task Failed_rollback_stops_all_automated_releases()
    {
        await DeployHealthyAsync(1, initialize: true);
        await guard.PromoteAsync(Request(2), Ct);
        await VerdictAsync(2, VerdictResult.Fail);
        await guard.RollbackAsync(Env, "run", Ct);
        await VerdictAsync(3, VerdictResult.Fail);

        Assert.Equal(EnvironmentState.RollbackFailed, (await guard.ReconcileAsync(Env, Ct))!.State);
        Assert.Equal(GuardOutcome.Blocked, (await guard.PromoteAsync(Request(4), Ct)).Outcome);
        Assert.Equal(GuardOutcome.Blocked, (await guard.RollbackAsync(Env, "run", Ct)).Outcome);

        await guard.GrantOverrideAsync(Env, Digest(4), "Override cannot bypass a failed rollback", TimeSpan.FromHours(1), "run", "owner", Ct);
        Assert.Equal(GuardOutcome.Blocked, (await guard.PromoteAsync(Request(4) with { UseOverride = true }, Ct)).Outcome);
    }

    [Fact]
    public async Task Override_lifts_the_block_for_one_digest_once()
    {
        await DeployHealthyAsync(1, initialize: true);
        await guard.PromoteAsync(Request(2), Ct);
        await VerdictAsync(2, VerdictResult.Fail);

        var withoutGrant = await guard.PromoteAsync(Request(3) with { UseOverride = true }, Ct);
        Assert.Equal(GuardOutcome.Blocked, withoutGrant.Outcome);

        await guard.GrantOverrideAsync(Env, Digest(3), "Hotfix for the failed release 2, approved", TimeSpan.FromHours(2), "https://run/approval", "owner", Ct);

        var otherDigest = await guard.PromoteAsync(Request(4) with { UseOverride = true }, Ct);
        Assert.Equal(GuardOutcome.Blocked, otherDigest.Outcome);

        var overridden = await guard.PromoteAsync(Request(3) with { UseOverride = true }, Ct);
        Assert.Equal(GuardOutcome.Started, overridden.Outcome);
        Assert.Equal(DeploymentKind.Override, overridden.Deployment!.Kind);
        Assert.Equal(3, (await store.GetOverrideAsync(Env, Digest(3), Ct))!.ConsumedBySeq);

        // Consumed: the same grant cannot start a second release.
        await VerdictAsync(3, VerdictResult.Fail);
        Assert.Equal(GuardOutcome.Blocked, (await guard.PromoteAsync(Request(3) with { UseOverride = true, SourceCommit = Commit(30) }, Ct)).Outcome);
    }

    [Fact]
    public async Task Expired_override_is_refused()
    {
        await DeployHealthyAsync(1, initialize: true);
        await guard.PromoteAsync(Request(2), Ct);
        await VerdictAsync(2, VerdictResult.Fail);
        await guard.GrantOverrideAsync(Env, Digest(3), "Short-lived approval for a hotfix", TimeSpan.FromMinutes(30), "run", "owner", Ct);

        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(GuardOutcome.Blocked, (await guard.PromoteAsync(Request(3) with { UseOverride = true }, Ct)).Outcome);
    }

    [Fact]
    public async Task Override_still_requires_passing_scan_evidence()
    {
        await DeployHealthyAsync(1, initialize: true);
        await guard.PromoteAsync(Request(2), Ct);
        await VerdictAsync(2, VerdictResult.Fail);
        await guard.GrantOverrideAsync(Env, Digest(3), "Approved, but the image is vulnerable", TimeSpan.FromHours(1), "run", "owner", Ct);

        var request = Request(3) with { UseOverride = true, Scan = new ScanEvidence(Digest(3), "fail", new string('b', 64)) };
        Assert.Equal(GuardOutcome.Blocked, (await guard.PromoteAsync(request, Ct)).Outcome);
    }

    [Fact]
    public async Task First_release_failing_leaves_only_the_override_path()
    {
        await guard.PromoteAsync(Request(1, initialize: true), Ct);
        await VerdictAsync(1, VerdictResult.Fail);

        var rollback = await guard.RollbackAsync(Env, "run", Ct);
        Assert.Equal(GuardOutcome.Blocked, rollback.Outcome);
        Assert.Contains("nothing to roll back", rollback.Reason, StringComparison.Ordinal);

        await guard.GrantOverrideAsync(Env, Digest(2), "Fix for the failed first deployment", TimeSpan.FromHours(1), "run", "owner", Ct);
        Assert.Equal(GuardOutcome.Started, (await guard.PromoteAsync(Request(2) with { UseOverride = true }, Ct)).Outcome);
    }

    [Theory]
    [InlineData("too short", 1)]
    [InlineData("A perfectly adequate reason for the override", 25)]
    [InlineData("A perfectly adequate reason for the override", 0)]
    public async Task Override_grants_are_validated(string reason, int hours)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            guard.GrantOverrideAsync(Env, Digest(1), reason, TimeSpan.FromHours(hours), "run", "owner", Ct));
    }

    [Fact]
    public async Task Concurrent_promotions_cannot_both_start()
    {
        await DeployHealthyAsync(1, initialize: true);

        // Both promotions read the same lock version before either writes.
        var racing = new RacingStore(store, readers: 2);
        var racingGuard = new DeploymentGuard(racing, clock, new GuardOptions());

        var results = await Task.WhenAll(
            racingGuard.PromoteAsync(Request(2), Ct),
            racingGuard.PromoteAsync(Request(3), Ct));

        Assert.Single(results, r => r.Outcome == GuardOutcome.Started);
        Assert.Single(results, r => r.Outcome == GuardOutcome.Blocked);
        Assert.Equal(2, (await store.GetLockAsync(Env, Ct))!.LastSeq);
    }

    [Fact]
    public async Task Wait_returns_the_verdict_once_the_verifier_rules()
    {
        await guard.PromoteAsync(Request(1, initialize: true), Ct);
        var waiting = guard.WaitAsync(Env, 1, TimeSpan.FromMinutes(12), TimeSpan.FromSeconds(10), Ct);

        Assert.False(waiting.IsCompleted);
        await VerdictAsync(1, VerdictResult.Pass);
        clock.Advance(TimeSpan.FromSeconds(10));

        var result = await waiting;
        Assert.False(result.TimedOut);
        Assert.Equal(EnvironmentState.Healthy, result.Lock.State);
        Assert.Equal(VerdictResult.Pass, result.Verification!.Result);
    }

    [Fact]
    public async Task Wait_times_out_without_settling_the_lock()
    {
        await guard.PromoteAsync(Request(1, initialize: true), Ct);
        var waiting = guard.WaitAsync(Env, 1, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), Ct);
        for (var i = 0; i < 4 && !waiting.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Yield();
        }

        var result = await waiting;
        Assert.True(result.TimedOut);
        Assert.Equal(EnvironmentState.Deploying, result.Lock.State);
    }

    private async Task DeployHealthyAsync(int n, bool initialize = false)
    {
        var decision = await guard.PromoteAsync(Request(n, initialize), Ct);
        Assert.Equal(GuardOutcome.Started, decision.Outcome);
        await VerdictAsync(decision.Deployment!.Seq, VerdictResult.Pass);
        Assert.Equal(EnvironmentState.Healthy, (await guard.ReconcileAsync(Env, Ct))!.State);
    }

    private Task VerdictAsync(long seq, VerdictResult result) =>
        store.PutVerificationAsync(
            new VerificationRecord(Env, seq, result, null, null, result.ToString(), clock.GetUtcNow(), clock.GetUtcNow(), []),
            Ct);

    private static PromoteRequest Request(int n, bool initialize = false) =>
        new(Env, Commit(n), Repo, Digest(n), new ScanEvidence(Digest(n), "pass", new string('c', 64)),
            Digest(100 + n), n, Commit(500 + n), $"https://run/{n}", initialize);

    internal static string Digest(int n) => "sha256:" + n.ToString("x64", System.Globalization.CultureInfo.InvariantCulture);

    private static string Commit(int n) => n.ToString("x40", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Holds lock reads until every racer has read, so all see the same version.</summary>
    private sealed class RacingStore(IDeploymentStore inner, int readers) : IDeploymentStore
    {
        private readonly TaskCompletionSource allRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int reads;

        public async Task<LockRecord?> GetLockAsync(string environment, CancellationToken cancellationToken)
        {
            var value = await inner.GetLockAsync(environment, cancellationToken);
            if (Interlocked.Increment(ref reads) >= readers)
            {
                allRead.TrySetResult();
            }

            await allRead.Task.WaitAsync(cancellationToken);
            return value;
        }

        public Task<DeploymentRecord?> GetDeploymentAsync(string environment, long seq, CancellationToken cancellationToken) =>
            inner.GetDeploymentAsync(environment, seq, cancellationToken);

        public Task<VerificationRecord?> GetVerificationAsync(string environment, long seq, CancellationToken cancellationToken) =>
            inner.GetVerificationAsync(environment, seq, cancellationToken);

        public Task<OverrideGrant?> GetOverrideAsync(string environment, string imageDigest, CancellationToken cancellationToken) =>
            inner.GetOverrideAsync(environment, imageDigest, cancellationToken);

        public Task<LockRecord> BeginDeploymentAsync(LockRecord? expected, DeploymentRecord deployment, EnvironmentState newState, string leaseOwner, DateTimeOffset leaseUntil, string? consumeOverrideDigest, CancellationToken cancellationToken) =>
            inner.BeginDeploymentAsync(expected, deployment, newState, leaseOwner, leaseUntil, consumeOverrideDigest, cancellationToken);

        public Task SetArtifactDigestAsync(string environment, long seq, string artifactDigest, CancellationToken cancellationToken) =>
            inner.SetArtifactDigestAsync(environment, seq, artifactDigest, cancellationToken);

        public Task<LockRecord> SettleAsync(LockRecord expected, EnvironmentState newState, long? lastHealthySeq, CancellationToken cancellationToken) =>
            inner.SettleAsync(expected, newState, lastHealthySeq, cancellationToken);

        public Task PutVerificationAsync(VerificationRecord verification, CancellationToken cancellationToken) =>
            inner.PutVerificationAsync(verification, cancellationToken);

        public Task PutOverrideAsync(OverrideGrant grant, CancellationToken cancellationToken) =>
            inner.PutOverrideAsync(grant, cancellationToken);
    }
}
