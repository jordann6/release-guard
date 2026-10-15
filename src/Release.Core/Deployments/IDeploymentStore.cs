namespace Release.Core.Deployments;

/// <summary>
/// Durable deployment records with conditional writes (ADR-0006). Every method that
/// changes the lock takes the version the caller read and fails with
/// <see cref="ConcurrentUpdateException"/> if another writer got there first.
/// </summary>
public interface IDeploymentStore
{
    Task<LockRecord?> GetLockAsync(string environment, CancellationToken cancellationToken);

    Task<DeploymentRecord?> GetDeploymentAsync(string environment, long seq, CancellationToken cancellationToken);

    Task<VerificationRecord?> GetVerificationAsync(string environment, long seq, CancellationToken cancellationToken);

    Task<OverrideGrant?> GetOverrideAsync(string environment, string imageDigest, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically: moves the lock from <paramref name="expected"/> (or creates it when null)
    /// to <paramref name="newState"/> with the next sequence in flight, writes the deployment
    /// record, and consumes the override grant when one is named.
    /// </summary>
    Task<LockRecord> BeginDeploymentAsync(
        LockRecord? expected,
        DeploymentRecord deployment,
        EnvironmentState newState,
        string leaseOwner,
        DateTimeOffset leaseUntil,
        string? consumeOverrideDigest,
        CancellationToken cancellationToken);

    /// <summary>Records the published artifact digest. Writing a different digest twice fails.</summary>
    Task SetArtifactDigestAsync(string environment, long seq, string artifactDigest, CancellationToken cancellationToken);

    /// <summary>Moves the lock to a settled state after a verdict or timeout.</summary>
    Task<LockRecord> SettleAsync(
        LockRecord expected,
        EnvironmentState newState,
        long? lastHealthySeq,
        CancellationToken cancellationToken);

    /// <summary>Writes a verdict once. A second verdict for the same sequence fails.</summary>
    Task PutVerificationAsync(VerificationRecord verification, CancellationToken cancellationToken);

    /// <summary>Writes a grant unless an unconsumed one exists for the digest.</summary>
    Task PutOverrideAsync(OverrideGrant grant, CancellationToken cancellationToken);
}

/// <summary>A conditional write lost to a concurrent writer.</summary>
public sealed class ConcurrentUpdateException : Exception
{
    public ConcurrentUpdateException()
    {
    }

    public ConcurrentUpdateException(string message)
        : base(message)
    {
    }

    public ConcurrentUpdateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
