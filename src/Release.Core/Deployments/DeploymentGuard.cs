using System.Text.RegularExpressions;

namespace Release.Core.Deployments;

public sealed record GuardOptions
{
    /// <summary>How long a promotion holds the lock before a missing verdict counts as a timeout.</summary>
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan MaxOverrideLifetime { get; init; } = TimeSpan.FromHours(24);

    public int MinOverrideReasonLength { get; init; } = 20;
}

public sealed record PromoteRequest(
    string Environment,
    string SourceCommit,
    string ImageRepository,
    string ImageDigest,
    ScanEvidence Scan,
    string? SbomDigest,
    int? PrNumber,
    string? PrTree,
    string RunUrl,
    bool Initialize = false,
    bool UseOverride = false);

public enum GuardOutcome
{
    /// <summary>A new release holds the lock. Publish its artifact, then wait.</summary>
    Started,

    /// <summary>A retry of the release already in flight. Finish publishing if needed, then wait.</summary>
    Resumed,

    /// <summary>The release is refused. Nothing was written.</summary>
    Blocked,

    /// <summary>The image is already the verified release. Nothing to do.</summary>
    NoChange,
}

public sealed record GuardDecision(GuardOutcome Outcome, string Reason, DeploymentRecord? Deployment = null)
{
    public static GuardDecision Block(string reason) => new(GuardOutcome.Blocked, reason);
}

public sealed record WaitResult(LockRecord Lock, VerificationRecord? Verification, bool TimedOut);

/// <summary>
/// The deployment guard (ADR-0006). Decides whether a release may start, records it,
/// and settles the environment from the verifier's verdict. It never declares a
/// release healthy on its own: only a verdict written by the verifier does that.
/// </summary>
public sealed partial class DeploymentGuard(IDeploymentStore store, TimeProvider clock, GuardOptions options)
{
    private const int MaxConflictRetries = 3;

    public async Task<LockRecord?> ReconcileAsync(string environment, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var current = await store.GetLockAsync(environment, cancellationToken).ConfigureAwait(false);
            if (current is null || !current.State.IsInFlight() || current.InflightSeq is not { } seq)
            {
                return current;
            }

            var verdict = await store.GetVerificationAsync(environment, seq, cancellationToken).ConfigureAwait(false);
            EnvironmentState next;
            long? lastHealthy = current.LastHealthySeq;

            if (verdict is not null)
            {
                next = (current.State, verdict.Result) switch
                {
                    (_, VerdictResult.Pass) => EnvironmentState.Healthy,
                    (EnvironmentState.RollingBack, _) => EnvironmentState.RollbackFailed,
                    (_, VerdictResult.Timeout) => EnvironmentState.VerifyTimeout,
                    _ => EnvironmentState.Failed,
                };
                if (verdict.Result == VerdictResult.Pass)
                {
                    lastHealthy = seq;
                }
            }
            else if (current.LeaseUntil is { } until && clock.GetUtcNow() > until)
            {
                // The run that held the lock is gone and the verifier never ruled.
                next = current.State == EnvironmentState.RollingBack
                    ? EnvironmentState.RollbackFailed
                    : EnvironmentState.VerifyTimeout;
            }
            else
            {
                return current;
            }

            try
            {
                return await store.SettleAsync(current, next, lastHealthy, cancellationToken).ConfigureAwait(false);
            }
            catch (ConcurrentUpdateException) when (attempt < MaxConflictRetries)
            {
                // Someone else settled or moved the lock first. Re-read and decide again.
            }
        }
    }

    public async Task<GuardDecision> PromoteAsync(PromoteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsDigest(request.ImageDigest))
        {
            return GuardDecision.Block($"'{request.ImageDigest}' is not an image digest.");
        }

        if (!request.Scan.Passed)
        {
            return GuardDecision.Block($"Scan evidence for {request.ImageDigest} did not pass.");
        }

        if (!string.Equals(request.Scan.ImageDigest, request.ImageDigest, StringComparison.Ordinal))
        {
            return GuardDecision.Block(
                $"Scan evidence is for {request.Scan.ImageDigest}, not the image being promoted ({request.ImageDigest}).");
        }

        var current = await ReconcileAsync(request.Environment, cancellationToken).ConfigureAwait(false);

        if (current is null)
        {
            return request.Initialize
                ? await StartAsync(null, request, DeploymentKind.Initialize, EnvironmentState.Deploying, null, null, cancellationToken).ConfigureAwait(false)
                : GuardDecision.Block($"Environment {request.Environment} has never been deployed. The first release needs --initialize.");
        }

        if (request.Initialize)
        {
            return GuardDecision.Block($"Environment {request.Environment} is already initialized.");
        }

        switch (current.State)
        {
            case EnvironmentState.Deploying or EnvironmentState.RollingBack:
                return await ResumeOrBlockAsync(current, request, cancellationToken).ConfigureAwait(false);

            case EnvironmentState.RollbackFailed:
                return GuardDecision.Block(
                    $"The rollback in release {current.LastSeq} failed verification. Automated releases are stopped; follow the break-glass runbook.");

            case EnvironmentState.Failed or EnvironmentState.VerifyTimeout:
                if (!request.UseOverride)
                {
                    return GuardDecision.Block(
                        $"Release {current.LastSeq} ended {current.State}. Roll back to the last verified release, or promote with an approved override.");
                }

                var grant = await store.GetOverrideAsync(request.Environment, request.ImageDigest, cancellationToken).ConfigureAwait(false);
                if (grant is null || !grant.IsUsable(clock.GetUtcNow()))
                {
                    return GuardDecision.Block(
                        $"No unexpired, unused override exists for {request.ImageDigest}.");
                }

                return await StartAsync(current, request, DeploymentKind.Override, EnvironmentState.Deploying, null, request.ImageDigest, cancellationToken).ConfigureAwait(false);

            case EnvironmentState.Healthy:
                if (current.LastHealthySeq is { } healthySeq)
                {
                    var healthy = await store.GetDeploymentAsync(request.Environment, healthySeq, cancellationToken).ConfigureAwait(false);
                    if (healthy is not null && string.Equals(healthy.ImageDigest, request.ImageDigest, StringComparison.Ordinal))
                    {
                        return new GuardDecision(GuardOutcome.NoChange, $"{request.ImageDigest} is already the verified release {healthySeq}.", healthy);
                    }
                }

                return await StartAsync(current, request, DeploymentKind.Normal, EnvironmentState.Deploying, null, null, cancellationToken).ConfigureAwait(false);

            default:
                return GuardDecision.Block($"Unexpected state {current.State}.");
        }
    }

    /// <summary>
    /// Restores the last verified release as a new, higher release, so desired state moves
    /// to healthy content and Flux does not reapply the failed one (ADR-0003, ADR-0006).
    /// </summary>
    public async Task<GuardDecision> RollbackAsync(string environment, string runUrl, CancellationToken cancellationToken)
    {
        var current = await ReconcileAsync(environment, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return GuardDecision.Block($"Environment {environment} has never been deployed.");
        }

        if (current.State == EnvironmentState.RollingBack && current.InflightSeq is { } inflightSeq)
        {
            var inflight = await store.GetDeploymentAsync(environment, inflightSeq, cancellationToken).ConfigureAwait(false);
            return new GuardDecision(GuardOutcome.Resumed, $"Rollback release {inflightSeq} is already in flight.", inflight);
        }

        if (!current.State.IsBroken())
        {
            return GuardDecision.Block($"Rollback is only for a failed or timed-out release; {environment} is {current.State}.");
        }

        if (current.LastHealthySeq is not { } healthySeq)
        {
            return GuardDecision.Block(
                "No release has ever been verified healthy, so there is nothing to roll back to. Promote a fix with an approved override.");
        }

        var verdict = await store.GetVerificationAsync(environment, healthySeq, cancellationToken).ConfigureAwait(false);
        var target = await store.GetDeploymentAsync(environment, healthySeq, cancellationToken).ConfigureAwait(false);
        if (verdict?.Result != VerdictResult.Pass || target is null)
        {
            return GuardDecision.Block($"Release {healthySeq} has no passing verification record.");
        }

        var restore = new PromoteRequest(
            environment,
            target.SourceCommit,
            target.ImageRepository,
            target.ImageDigest,
            target.Scan,
            target.SbomDigest,
            target.PrNumber,
            target.PrTree,
            runUrl);

        return await StartAsync(current, restore, DeploymentKind.Rollback, EnvironmentState.RollingBack, healthySeq, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OverrideGrant> GrantOverrideAsync(
        string environment,
        string imageDigest,
        string reason,
        TimeSpan lifetime,
        string approvedRunUrl,
        string actor,
        CancellationToken cancellationToken)
    {
        if (!IsDigest(imageDigest))
        {
            throw new ArgumentException($"'{imageDigest}' is not an image digest.", nameof(imageDigest));
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < options.MinOverrideReasonLength)
        {
            throw new ArgumentException($"An override needs a reason of at least {options.MinOverrideReasonLength} characters.", nameof(reason));
        }

        if (lifetime <= TimeSpan.Zero || lifetime > options.MaxOverrideLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), $"Override lifetime must be positive and at most {options.MaxOverrideLifetime}.");
        }

        var grant = new OverrideGrant(environment, imageDigest, reason.Trim(), clock.GetUtcNow() + lifetime, approvedRunUrl, actor);
        await store.PutOverrideAsync(grant, cancellationToken).ConfigureAwait(false);
        return grant;
    }

    /// <summary>Polls until the verifier rules on <paramref name="seq"/> or the timeout passes.</summary>
    public async Task<WaitResult> WaitAsync(
        string environment,
        long seq,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        var deadline = clock.GetUtcNow() + timeout;
        while (true)
        {
            var current = await ReconcileAsync(environment, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Environment {environment} has no lock.");

            if (!current.State.IsInFlight() || current.InflightSeq != seq)
            {
                var verdict = await store.GetVerificationAsync(environment, seq, cancellationToken).ConfigureAwait(false);
                return new WaitResult(current, verdict, TimedOut: false);
            }

            if (clock.GetUtcNow() >= deadline)
            {
                return new WaitResult(current, null, TimedOut: true);
            }

            await Task.Delay(pollInterval, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<GuardDecision> ResumeOrBlockAsync(LockRecord current, PromoteRequest request, CancellationToken cancellationToken)
    {
        if (current.InflightSeq is not { } seq)
        {
            return GuardDecision.Block($"Lock is {current.State} with no release in flight.");
        }

        var inflight = await store.GetDeploymentAsync(request.Environment, seq, cancellationToken).ConfigureAwait(false);
        if (current.State == EnvironmentState.Deploying
            && inflight is not null
            && string.Equals(inflight.SourceCommit, request.SourceCommit, StringComparison.Ordinal)
            && string.Equals(inflight.ImageDigest, request.ImageDigest, StringComparison.Ordinal))
        {
            // Same commit and image: a rerun of the same release. Do not publish a new one.
            return new GuardDecision(GuardOutcome.Resumed, $"Release {seq} for this commit is already in flight.", inflight);
        }

        return GuardDecision.Block($"Release {seq} is still {current.State}. Wait for its verdict.");
    }

    private async Task<GuardDecision> StartAsync(
        LockRecord? current,
        PromoteRequest request,
        DeploymentKind kind,
        EnvironmentState state,
        long? restoresSeq,
        string? consumeOverrideDigest,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var deployment = new DeploymentRecord(
            request.Environment,
            (current?.LastSeq ?? 0) + 1,
            kind,
            request.SourceCommit,
            request.ImageRepository,
            request.ImageDigest,
            request.Scan,
            request.SbomDigest,
            request.PrNumber,
            request.PrTree,
            request.RunUrl,
            now,
            restoresSeq);

        try
        {
            await store.BeginDeploymentAsync(
                current, deployment, state, request.RunUrl, now + options.Lease, consumeOverrideDigest, cancellationToken).ConfigureAwait(false);
        }
        catch (ConcurrentUpdateException)
        {
            return GuardDecision.Block("Another release changed the environment at the same moment. Nothing was published; retry.");
        }

        var verb = kind switch
        {
            DeploymentKind.Rollback => $"Rolling back to release {restoresSeq}",
            DeploymentKind.Override => "Promoting under an approved override",
            DeploymentKind.Initialize => "Initializing the environment",
            _ => "Promoting",
        };
        return new GuardDecision(GuardOutcome.Started, $"{verb} as release {deployment.Seq}.", deployment);
    }

    internal static bool IsDigest(string? value) => value is not null && DigestPattern().IsMatch(value);

    [GeneratedRegex("^sha256:[a-f0-9]{64}$")]
    private static partial Regex DigestPattern();
}
