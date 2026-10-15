namespace Release.Core.Deployments;

/// <summary>States of an environment's deployment lock (ADR-0006).</summary>
public enum EnvironmentState
{
    Uninitialized,
    Deploying,
    Healthy,
    Failed,
    VerifyTimeout,
    RollingBack,
    RollbackFailed,
}

public enum DeploymentKind
{
    Initialize,
    Normal,
    Rollback,
    Override,
}

public enum VerdictResult
{
    Pass,
    Fail,
    Timeout,
}

public static class EnvironmentStates
{
    /// <summary>A release is published and the verifier has not yet ruled on it.</summary>
    public static bool IsInFlight(this EnvironmentState state) =>
        state is EnvironmentState.Deploying or EnvironmentState.RollingBack;

    /// <summary>The latest release did not prove healthy; ordinary promotion is blocked.</summary>
    public static bool IsBroken(this EnvironmentState state) =>
        state is EnvironmentState.Failed or EnvironmentState.VerifyTimeout;
}

/// <summary>
/// The single item that serializes deployments for one environment. Every write is
/// conditional on <see cref="Version"/>, and the release sequence lives here too, so
/// taking the lock and allocating a sequence number cannot disagree.
/// </summary>
public sealed record LockRecord(
    string Environment,
    EnvironmentState State,
    long LastSeq,
    long? InflightSeq,
    string? LeaseOwner,
    DateTimeOffset? LeaseUntil,
    long? LastHealthySeq,
    long Version);

/// <summary>Image scan result that a release must carry (ADR-0006, ADR-0007).</summary>
public sealed record ScanEvidence(string ImageDigest, string Result, string ReportSha256)
{
    public bool Passed => string.Equals(Result, "pass", StringComparison.Ordinal);
}

/// <summary>What was deployed, from what, and on what evidence. Written once per release.</summary>
public sealed record DeploymentRecord(
    string Environment,
    long Seq,
    DeploymentKind Kind,
    string SourceCommit,
    string ImageRepository,
    string ImageDigest,
    ScanEvidence Scan,
    string? SbomDigest,
    int? PrNumber,
    string? PrTree,
    string RunUrl,
    DateTimeOffset CreatedAt,
    long? RestoresSeq = null,
    string? ArtifactDigest = null)
{
    /// <summary>Release channel tag in the deployment repository (ADR-0003).</summary>
    public string ArtifactTag => ArtifactTags.ForSeq(Seq);

    public string ImageReference => $"{ImageRepository}@{ImageDigest}";
}

public static class ArtifactTags
{
    public static string ForSeq(long seq) => $"1.0.{seq}";
}

public sealed record SmokeStep(string Name, bool Passed, string Detail);

/// <summary>The verifier's ruling. Only the in-cluster verifier identity can write these.</summary>
public sealed record VerificationRecord(
    string Environment,
    long Seq,
    VerdictResult Result,
    string? ObservedArtifactRevision,
    string? ObservedImage,
    string Reason,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<SmokeStep> Smoke);

/// <summary>An approved exception for one image digest, consumed by exactly one deployment.</summary>
public sealed record OverrideGrant(
    string Environment,
    string ImageDigest,
    string Reason,
    DateTimeOffset ExpiresAt,
    string ApprovedRunUrl,
    string Actor,
    long? ConsumedBySeq = null)
{
    public bool IsUsable(DateTimeOffset now) => ConsumedBySeq is null && now < ExpiresAt;
}
