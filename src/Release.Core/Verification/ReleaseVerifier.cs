using Microsoft.Extensions.Logging;
using Release.Core.Deployments;

namespace Release.Core.Verification;

/// <summary>What Flux reports for the application Kustomization.</summary>
public sealed record KustomizationStatus(
    bool Ready,
    string? Reason,
    string? Message,
    string? LastAppliedRevision,
    string? LastAttemptedRevision);

/// <summary>What Kubernetes reports for the application Deployment.</summary>
public sealed record RolloutStatus(
    string? Image,
    long Generation,
    long ObservedGeneration,
    int Desired,
    int Updated,
    int Ready,
    int Available,
    bool ProgressDeadlineExceeded);

public interface IClusterObserver
{
    Task<KustomizationStatus?> GetKustomizationAsync(CancellationToken cancellationToken);

    Task<RolloutStatus?> GetRolloutAsync(CancellationToken cancellationToken);
}

public interface ISmokeTest
{
    Task<IReadOnlyList<SmokeStep>> RunAsync(long seq, CancellationToken cancellationToken);
}

public sealed record VerifierOptions
{
    public required string Environment { get; init; }

    /// <summary>Time from the release record's creation to a verdict (ADR-0006).</summary>
    public TimeSpan Deadline { get; init; } = TimeSpan.FromMinutes(10);
}

public enum TickOutcome
{
    Idle,
    Waiting,
    Recorded,
}

public sealed record TickResult(TickOutcome Outcome, string Detail, VerificationRecord? Verdict = null);

/// <summary>
/// The in-cluster verifier (ADR-0006). For the release in flight it confirms that Flux
/// applied the expected artifact, that the Deployment runs the expected image digest and
/// finished rolling out, and that the service passes a functional smoke test. Only then
/// does it record a pass. Publishing manifests is never treated as success.
/// </summary>
public sealed partial class ReleaseVerifier(
    IDeploymentStore store,
    IClusterObserver cluster,
    ISmokeTest smoke,
    TimeProvider clock,
    VerifierOptions options,
    ILogger<ReleaseVerifier> logger)
{
    // Kustomization Ready=False reasons that mean the applied revision failed, not that it is still progressing.
    private static readonly HashSet<string> FailureReasons = new(StringComparer.Ordinal)
    {
        "HealthCheckFailed",
        "ReconciliationFailed",
        "BuildFailed",
        "ArtifactFailed",
    };

    private string lastObservation = "nothing observed yet";

    public async Task<TickResult> TickAsync(CancellationToken cancellationToken)
    {
        var current = await store.GetLockAsync(options.Environment, cancellationToken).ConfigureAwait(false);
        if (current is null || !current.State.IsInFlight() || current.InflightSeq is not { } seq)
        {
            return new TickResult(TickOutcome.Idle, "No release in flight.");
        }

        if (await store.GetVerificationAsync(options.Environment, seq, cancellationToken).ConfigureAwait(false) is not null)
        {
            return new TickResult(TickOutcome.Idle, $"Release {seq} already has a verdict.");
        }

        var deployment = await store.GetDeploymentAsync(options.Environment, seq, cancellationToken).ConfigureAwait(false);
        if (deployment is null)
        {
            return new TickResult(TickOutcome.Waiting, $"Release {seq} has no record yet.");
        }

        var now = clock.GetUtcNow();
        if (now > deployment.CreatedAt + options.Deadline)
        {
            return await RecordAsync(deployment, VerdictResult.Timeout, $"No healthy rollout within {options.Deadline}. Last observation: {lastObservation}", null, null, [], cancellationToken).ConfigureAwait(false);
        }

        if (deployment.ArtifactDigest is not { } artifactDigest)
        {
            return Wait($"Release {seq} artifact is not published yet.");
        }

        var expectedSuffix = "@" + artifactDigest;
        var kustomization = await cluster.GetKustomizationAsync(cancellationToken).ConfigureAwait(false);
        if (kustomization is null)
        {
            return Wait("Flux Kustomization not found.");
        }

        if (!kustomization.Ready
            && EndsWith(kustomization.LastAttemptedRevision, expectedSuffix)
            && kustomization.Reason is { } reason
            && FailureReasons.Contains(reason))
        {
            return await RecordAsync(deployment, VerdictResult.Fail, $"Flux failed to apply release {seq}: {reason}: {kustomization.Message}", kustomization.LastAttemptedRevision, null, [], cancellationToken).ConfigureAwait(false);
        }

        if (!kustomization.Ready || !EndsWith(kustomization.LastAppliedRevision, expectedSuffix))
        {
            return Wait($"Flux has applied {kustomization.LastAppliedRevision ?? "nothing"}, waiting for {deployment.ArtifactTag}{expectedSuffix} ({kustomization.Reason}).");
        }

        var rollout = await cluster.GetRolloutAsync(cancellationToken).ConfigureAwait(false);
        if (rollout is null)
        {
            return Wait("Application Deployment not found.");
        }

        if (!string.Equals(rollout.Image, deployment.ImageReference, StringComparison.Ordinal))
        {
            return await RecordAsync(deployment, VerdictResult.Fail, $"Applied revision runs {rollout.Image}, expected {deployment.ImageReference}.", kustomization.LastAppliedRevision, rollout.Image, [], cancellationToken).ConfigureAwait(false);
        }

        if (rollout.ProgressDeadlineExceeded)
        {
            return await RecordAsync(deployment, VerdictResult.Fail, $"Rollout exceeded its progress deadline ({rollout.Available}/{rollout.Desired} available).", kustomization.LastAppliedRevision, rollout.Image, [], cancellationToken).ConfigureAwait(false);
        }

        var complete = rollout.ObservedGeneration >= rollout.Generation
            && rollout.Updated == rollout.Desired
            && rollout.Ready == rollout.Desired
            && rollout.Available == rollout.Desired
            && rollout.Desired > 0;
        if (!complete)
        {
            return Wait($"Rollout in progress: {rollout.Updated} updated, {rollout.Ready} ready, {rollout.Available}/{rollout.Desired} available.");
        }

        var steps = await smoke.RunAsync(seq, cancellationToken).ConfigureAwait(false);
        var failed = steps.FirstOrDefault(s => !s.Passed);
        return failed is null
            ? await RecordAsync(deployment, VerdictResult.Pass, $"Revision {kustomization.LastAppliedRevision} rolled out and passed {steps.Count} smoke steps.", kustomization.LastAppliedRevision, rollout.Image, steps, cancellationToken).ConfigureAwait(false)
            : await RecordAsync(deployment, VerdictResult.Fail, $"Smoke step '{failed.Name}' failed: {failed.Detail}", kustomization.LastAppliedRevision, rollout.Image, steps, cancellationToken).ConfigureAwait(false);
    }

    private TickResult Wait(string observation)
    {
        lastObservation = observation;
        LogWaiting(logger, observation);
        return new TickResult(TickOutcome.Waiting, observation);
    }

    private async Task<TickResult> RecordAsync(
        DeploymentRecord deployment,
        VerdictResult result,
        string reason,
        string? observedRevision,
        string? observedImage,
        IReadOnlyList<SmokeStep> steps,
        CancellationToken cancellationToken)
    {
        var verdict = new VerificationRecord(
            deployment.Environment,
            deployment.Seq,
            result,
            observedRevision,
            observedImage,
            reason,
            deployment.CreatedAt,
            clock.GetUtcNow(),
            steps);

        try
        {
            await store.PutVerificationAsync(verdict, cancellationToken).ConfigureAwait(false);
        }
        catch (ConcurrentUpdateException)
        {
            return new TickResult(TickOutcome.Idle, $"Release {deployment.Seq} was ruled on by another verifier instance.");
        }

        lastObservation = "nothing observed yet";
        LogVerdict(logger, deployment.Seq, result, reason);
        return new TickResult(TickOutcome.Recorded, reason, verdict);
    }

    private static bool EndsWith(string? value, string suffix) =>
        value is not null && value.EndsWith(suffix, StringComparison.Ordinal);

    [LoggerMessage(Level = LogLevel.Information, Message = "Waiting: {Observation}")]
    private static partial void LogWaiting(ILogger logger, string observation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Release {Seq} verdict {Result}: {Reason}")]
    private static partial void LogVerdict(ILogger logger, long seq, VerdictResult result, string reason);
}
