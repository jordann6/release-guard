namespace Release.Core.Deployments;

/// <summary>
/// Store with the same conditional-write semantics as the DynamoDB store, for unit tests
/// and dry runs. Every write is checked and applied under one lock, so a lost race
/// throws <see cref="ConcurrentUpdateException"/> exactly as a failed DynamoDB condition does.
/// </summary>
public sealed class InMemoryDeploymentStore : IDeploymentStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, LockRecord> locks = [];
    private readonly Dictionary<(string, long), DeploymentRecord> deployments = [];
    private readonly Dictionary<(string, long), VerificationRecord> verifications = [];
    private readonly Dictionary<(string, string), OverrideGrant> overrides = [];

    public Task<LockRecord?> GetLockAsync(string environment, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(locks.GetValueOrDefault(environment));
        }
    }

    public Task<DeploymentRecord?> GetDeploymentAsync(string environment, long seq, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(deployments.GetValueOrDefault((environment, seq)));
        }
    }

    public Task<VerificationRecord?> GetVerificationAsync(string environment, long seq, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(verifications.GetValueOrDefault((environment, seq)));
        }
    }

    public Task<OverrideGrant?> GetOverrideAsync(string environment, string imageDigest, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(overrides.GetValueOrDefault((environment, imageDigest)));
        }
    }

    public Task<LockRecord> BeginDeploymentAsync(
        LockRecord? expected,
        DeploymentRecord deployment,
        EnvironmentState newState,
        string leaseOwner,
        DateTimeOffset leaseUntil,
        string? consumeOverrideDigest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        lock (gate)
        {
            var env = deployment.Environment;
            var current = locks.GetValueOrDefault(env);
            if (expected is null ? current is not null : current?.Version != expected.Version)
            {
                throw new ConcurrentUpdateException($"Lock for {env} changed.");
            }

            if (deployment.Seq != (current?.LastSeq ?? 0) + 1 || deployments.ContainsKey((env, deployment.Seq)))
            {
                throw new ConcurrentUpdateException($"Release {deployment.Seq} already exists.");
            }

            OverrideGrant? grant = null;
            if (consumeOverrideDigest is not null)
            {
                grant = overrides.GetValueOrDefault((env, consumeOverrideDigest));
                if (grant is null || grant.ConsumedBySeq is not null || grant.ExpiresAt <= deployment.CreatedAt)
                {
                    throw new ConcurrentUpdateException($"Override for {consumeOverrideDigest} is not usable.");
                }
            }

            var next = new LockRecord(
                env,
                newState,
                deployment.Seq,
                deployment.Seq,
                leaseOwner,
                leaseUntil,
                current?.LastHealthySeq,
                (current?.Version ?? 0) + 1);

            locks[env] = next;
            deployments[(env, deployment.Seq)] = deployment;
            if (grant is not null)
            {
                overrides[(env, grant.ImageDigest)] = grant with { ConsumedBySeq = deployment.Seq };
            }

            return Task.FromResult(next);
        }
    }

    public Task SetArtifactDigestAsync(string environment, long seq, string artifactDigest, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!deployments.TryGetValue((environment, seq), out var deployment))
            {
                throw new InvalidOperationException($"Release {seq} does not exist.");
            }

            if (deployment.ArtifactDigest is not null && deployment.ArtifactDigest != artifactDigest)
            {
                throw new ConcurrentUpdateException($"Release {seq} already has artifact {deployment.ArtifactDigest}.");
            }

            deployments[(environment, seq)] = deployment with { ArtifactDigest = artifactDigest };
            return Task.CompletedTask;
        }
    }

    public Task<LockRecord> SettleAsync(
        LockRecord expected,
        EnvironmentState newState,
        long? lastHealthySeq,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        lock (gate)
        {
            var current = locks.GetValueOrDefault(expected.Environment);
            if (current?.Version != expected.Version)
            {
                throw new ConcurrentUpdateException($"Lock for {expected.Environment} changed.");
            }

            var next = current with
            {
                State = newState,
                InflightSeq = null,
                LeaseOwner = null,
                LeaseUntil = null,
                LastHealthySeq = lastHealthySeq,
                Version = current.Version + 1,
            };
            locks[expected.Environment] = next;
            return Task.FromResult(next);
        }
    }

    public Task PutVerificationAsync(VerificationRecord verification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verification);
        lock (gate)
        {
            if (!verifications.TryAdd((verification.Environment, verification.Seq), verification))
            {
                throw new ConcurrentUpdateException($"Release {verification.Seq} already has a verdict.");
            }

            return Task.CompletedTask;
        }
    }

    public Task PutOverrideAsync(OverrideGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (gate)
        {
            var key = (grant.Environment, grant.ImageDigest);
            if (overrides.TryGetValue(key, out var existing) && existing.ConsumedBySeq is null && existing.ExpiresAt > DateTimeOffset.UtcNow)
            {
                throw new ConcurrentUpdateException($"An unused override already exists for {grant.ImageDigest}.");
            }

            overrides[key] = grant;
            return Task.CompletedTask;
        }
    }
}
