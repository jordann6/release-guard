using Microsoft.Extensions.Time.Testing;
using Release.Core.Deployments;

namespace Release.IntegrationTests;

/// <summary>
/// Behavior every store must share. Run against DynamoDB Local and the in-memory store,
/// so the fast fake used by unit tests cannot drift from the real conditional writes.
/// </summary>
public abstract class StoreContractTests
{
    protected const string Env = "prod";

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract Task<IDeploymentStore> CreateStoreAsync();

    [Fact]
    public async Task Begin_creates_lock_and_record_atomically()
    {
        var store = await CreateStoreAsync();
        var lockRecord = await store.BeginDeploymentAsync(null, Deployment(1), EnvironmentState.Deploying, "run-1", Now.AddMinutes(15), null, Ct);

        Assert.Equal(1, lockRecord.Version);
        var read = await store.GetLockAsync(Env, Ct);
        Assert.Equal(lockRecord, read);
        Assert.Equal(Deployment(1), await store.GetDeploymentAsync(Env, 1, Ct));
    }

    [Fact]
    public async Task Begin_fails_when_the_lock_already_exists_or_moved()
    {
        var store = await CreateStoreAsync();
        var first = await store.BeginDeploymentAsync(null, Deployment(1), EnvironmentState.Deploying, "a", Now, null, Ct);

        await Assert.ThrowsAsync<ConcurrentUpdateException>(() =>
            store.BeginDeploymentAsync(null, Deployment(1), EnvironmentState.Deploying, "b", Now, null, Ct));

        var settled = await store.SettleAsync(first, EnvironmentState.Healthy, 1, Ct);
        await Assert.ThrowsAsync<ConcurrentUpdateException>(() =>
            store.BeginDeploymentAsync(first, Deployment(2), EnvironmentState.Deploying, "c", Now, null, Ct));

        var second = await store.BeginDeploymentAsync(settled, Deployment(2), EnvironmentState.Deploying, "d", Now, null, Ct);
        Assert.Equal(2, second.LastSeq);
        Assert.Equal(1, second.LastHealthySeq);
        Assert.Equal(3, second.Version);
    }

    [Fact]
    public async Task Failed_begin_writes_nothing()
    {
        var store = await CreateStoreAsync();
        var first = await store.BeginDeploymentAsync(null, Deployment(1), EnvironmentState.Deploying, "a", Now, null, Ct);
        await store.SettleAsync(first, EnvironmentState.Healthy, 1, Ct);

        // Stale expected version: the record for release 2 must not appear.
        await Assert.ThrowsAsync<ConcurrentUpdateException>(() =>
            store.BeginDeploymentAsync(first, Deployment(2), EnvironmentState.Deploying, "b", Now, null, Ct));
        Assert.Null(await store.GetDeploymentAsync(Env, 2, Ct));
    }

    [Fact]
    public async Task Settle_is_conditional_on_version_and_clears_the_lease()
    {
        var store = await CreateStoreAsync();
        var begun = await store.BeginDeploymentAsync(null, Deployment(1), EnvironmentState.Deploying, "a", Now, null, Ct);
        var settled = await store.SettleAsync(begun, EnvironmentState.Failed, null, Ct);

        Assert.Null(settled.InflightSeq);
        Assert.Null(settled.LeaseUntil);
        Assert.Equal(settled, await store.GetLockAsync(Env, Ct));
        await Assert.ThrowsAsync<ConcurrentUpdateException>(() => store.SettleAsync(begun, EnvironmentState.Healthy, 1, Ct));
    }

    [Fact]
    public async Task Artifact_digest_is_write_once()
    {
        var store = await CreateStoreAsync();
        await store.BeginDeploymentAsync(null, Deployment(1), EnvironmentState.Deploying, "a", Now, null, Ct);

        await store.SetArtifactDigestAsync(Env, 1, Digest(9), Ct);
        await store.SetArtifactDigestAsync(Env, 1, Digest(9), Ct);
        await Assert.ThrowsAsync<ConcurrentUpdateException>(() => store.SetArtifactDigestAsync(Env, 1, Digest(8), Ct));
        Assert.Equal(Digest(9), (await store.GetDeploymentAsync(Env, 1, Ct))!.ArtifactDigest);
    }

    [Fact]
    public async Task A_verdict_is_written_once()
    {
        var store = await CreateStoreAsync();
        var verdict = new VerificationRecord(Env, 1, VerdictResult.Pass, "1.0.1@" + Digest(9), "repo@" + Digest(1), "ok", Now, Now.AddSeconds(30),
            [new SmokeStep("create", true, "201"), new SmokeStep("replay", true, "200")]);

        await store.PutVerificationAsync(verdict, Ct);
        await Assert.ThrowsAsync<ConcurrentUpdateException>(() => store.PutVerificationAsync(verdict with { Result = VerdictResult.Fail }, Ct));

        var read = await store.GetVerificationAsync(Env, 1, Ct);
        Assert.Equal(VerdictResult.Pass, read!.Result);
        Assert.Equal(verdict.Smoke, read.Smoke);
    }

    [Fact]
    public async Task Override_is_consumed_with_the_deployment_and_only_once()
    {
        var store = await CreateStoreAsync();
        var begun = await store.BeginDeploymentAsync(null, Deployment(1), EnvironmentState.Deploying, "a", Now, null, Ct);
        var failed = await store.SettleAsync(begun, EnvironmentState.Failed, null, Ct);

        await store.PutOverrideAsync(new OverrideGrant(Env, Digest(2), "reason", Now.AddHours(1), "run", "actor"), Ct);
        await Assert.ThrowsAsync<ConcurrentUpdateException>(() =>
            store.PutOverrideAsync(new OverrideGrant(Env, Digest(2), "again", Now.AddHours(1), "run", "actor"), Ct));

        var overridden = await store.BeginDeploymentAsync(failed, Deployment(2), EnvironmentState.Deploying, "b", Now, Digest(2), Ct);
        Assert.Equal(2, (await store.GetOverrideAsync(Env, Digest(2), Ct))!.ConsumedBySeq);

        var failedAgain = await store.SettleAsync(overridden, EnvironmentState.Failed, null, Ct);
        await Assert.ThrowsAsync<ConcurrentUpdateException>(() =>
            store.BeginDeploymentAsync(failedAgain, Deployment(3) with { ImageDigest = Digest(2) }, EnvironmentState.Deploying, "c", Now, Digest(2), Ct));
        Assert.Null(await store.GetDeploymentAsync(Env, 3, Ct));
    }

    [Fact]
    public async Task Expired_override_cannot_be_consumed()
    {
        var store = await CreateStoreAsync();
        var begun = await store.BeginDeploymentAsync(null, Deployment(1), EnvironmentState.Deploying, "a", Now, null, Ct);
        var failed = await store.SettleAsync(begun, EnvironmentState.Failed, null, Ct);
        await store.PutOverrideAsync(new OverrideGrant(Env, Digest(2), "reason", Now.AddMinutes(-1), "run", "actor"), Ct);

        await Assert.ThrowsAsync<ConcurrentUpdateException>(() =>
            store.BeginDeploymentAsync(failed, Deployment(2), EnvironmentState.Deploying, "b", Now, Digest(2), Ct));
    }

    [Fact]
    public async Task Full_release_cycle_through_the_guard()
    {
        var store = await CreateStoreAsync();
        var clock = new FakeTimeProvider(Now);
        var guard = new DeploymentGuard(store, clock, new GuardOptions());

        Assert.Equal(GuardOutcome.Started, (await guard.PromoteAsync(Request(1, initialize: true), Ct)).Outcome);
        await store.PutVerificationAsync(Verdict(1, VerdictResult.Pass), Ct);
        Assert.Equal(GuardOutcome.Started, (await guard.PromoteAsync(Request(2), Ct)).Outcome);
        await store.PutVerificationAsync(Verdict(2, VerdictResult.Fail), Ct);

        Assert.Equal(GuardOutcome.Blocked, (await guard.PromoteAsync(Request(3), Ct)).Outcome);

        var rollback = await guard.RollbackAsync(Env, "run", Ct);
        Assert.Equal(3, rollback.Deployment!.Seq);
        Assert.Equal(Digest(1), rollback.Deployment.ImageDigest);
        await store.PutVerificationAsync(Verdict(3, VerdictResult.Pass), Ct);

        var settled = await guard.ReconcileAsync(Env, Ct);
        Assert.Equal(EnvironmentState.Healthy, settled!.State);
        Assert.Equal(3, settled.LastHealthySeq);
    }

    protected static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    protected static string Digest(int n) => "sha256:" + n.ToString("x64", System.Globalization.CultureInfo.InvariantCulture);

    private static DeploymentRecord Deployment(long seq) => new(
        Env, seq, seq == 1 ? DeploymentKind.Initialize : DeploymentKind.Normal,
        new string('a', 40), "repo/app", Digest((int)seq),
        new ScanEvidence(Digest((int)seq), "pass", new string('f', 64)),
        Digest(100), 7, new string('b', 40), $"https://run/{seq}", Now);

    private static PromoteRequest Request(int n, bool initialize = false) =>
        new(Env, n.ToString("x40", System.Globalization.CultureInfo.InvariantCulture), "repo/app", Digest(n),
            new ScanEvidence(Digest(n), "pass", new string('f', 64)), null, n, null, $"https://run/{n}", initialize);

    private static VerificationRecord Verdict(long seq, VerdictResult result) =>
        new(Env, seq, result, null, null, result.ToString(), Now, Now, []);
}

public sealed class DynamoStoreContractTests(DynamoFixture dynamo) : StoreContractTests
{
    protected override async Task<IDeploymentStore> CreateStoreAsync() => await dynamo.NewStoreAsync();
}

public sealed class InMemoryStoreContractTests : StoreContractTests
{
    protected override Task<IDeploymentStore> CreateStoreAsync() => Task.FromResult<IDeploymentStore>(new InMemoryDeploymentStore());
}
