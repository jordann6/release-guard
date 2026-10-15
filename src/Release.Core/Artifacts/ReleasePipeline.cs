using System.Globalization;
using System.Text.Json.Nodes;
using Release.Core.Deployments;

namespace Release.Core.Artifacts;

public sealed record PublishSettings(string TemplateDirectory, string WorkDirectory, string SourceUrl);

public sealed record PipelineResult(GuardDecision Decision, string? ArtifactDigest)
{
    public bool Proceeding => Decision.Outcome is GuardOutcome.Started or GuardOutcome.Resumed;
}

/// <summary>
/// Promotion and rollback end to end: the guard takes the lock and records the release,
/// then the artifact is published and its digest recorded (ADR-0006: the lock is taken
/// before anything is published). Retrying after an interruption resumes the same release.
/// </summary>
public sealed class ReleasePipeline(DeploymentGuard guard, IDeploymentStore store, IArtifactPublisher publisher, PublishSettings settings)
{
    public async Task<PipelineResult> PromoteAsync(PromoteRequest request, CancellationToken cancellationToken)
    {
        var decision = await guard.PromoteAsync(request, cancellationToken).ConfigureAwait(false);
        if (decision.Outcome is not (GuardOutcome.Started or GuardOutcome.Resumed) || decision.Deployment is not { } deployment)
        {
            return new PipelineResult(decision, decision.Deployment?.ArtifactDigest);
        }

        if (deployment.ArtifactDigest is { } existing)
        {
            return new PipelineResult(decision, existing);
        }

        var directory = ManifestRenderer.Render(settings.TemplateDirectory, WorkPath(deployment), deployment);
        return new PipelineResult(decision, await PushAndRecordAsync(directory, deployment, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Republishes the exact artifact of the last verified release under the next version,
    /// so manifests are restored along with the image.
    /// </summary>
    public async Task<PipelineResult> RollbackAsync(string environment, string runUrl, CancellationToken cancellationToken)
    {
        var decision = await guard.RollbackAsync(environment, runUrl, cancellationToken).ConfigureAwait(false);
        if (decision.Outcome is not (GuardOutcome.Started or GuardOutcome.Resumed) || decision.Deployment is not { } deployment)
        {
            return new PipelineResult(decision, null);
        }

        if (deployment.ArtifactDigest is { } existing)
        {
            return new PipelineResult(decision, existing);
        }

        var restores = deployment.RestoresSeq ?? throw new InvalidOperationException($"Rollback release {deployment.Seq} names no release to restore.");
        var directory = WorkPath(deployment);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        await publisher.PullAsync(ArtifactTags.ForSeq(restores), directory, cancellationToken).ConfigureAwait(false);
        RewriteReleaseManifest(directory, deployment);
        return new PipelineResult(decision, await PushAndRecordAsync(directory, deployment, cancellationToken).ConfigureAwait(false));
    }

    private async Task<string> PushAndRecordAsync(string directory, DeploymentRecord deployment, CancellationToken cancellationToken)
    {
        var revision = $"{deployment.ArtifactTag}@sha1:{deployment.SourceCommit}";
        var digest = await publisher.PushAsync(directory, deployment.ArtifactTag, settings.SourceUrl, revision, cancellationToken).ConfigureAwait(false);
        await store.SetArtifactDigestAsync(deployment.Environment, deployment.Seq, digest, cancellationToken).ConfigureAwait(false);
        return digest;
    }

    private string WorkPath(DeploymentRecord deployment) =>
        Path.Combine(settings.WorkDirectory, $"{deployment.Environment}-{deployment.Seq.ToString(CultureInfo.InvariantCulture)}");

    private static void RewriteReleaseManifest(string directory, DeploymentRecord deployment)
    {
        var path = Path.Combine(directory, "release.json");
        var node = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : [];
        node["restored_from"] = node["tag"]?.DeepClone();
        node["seq"] = deployment.Seq;
        node["tag"] = deployment.ArtifactTag;
        node["kind"] = deployment.Kind.ToString();
        node["restores_seq"] = deployment.RestoresSeq;
        File.WriteAllText(path, node.ToJsonString(ManifestRenderer.Indented));
    }
}
