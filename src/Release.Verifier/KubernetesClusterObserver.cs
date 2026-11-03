using System.Net;
using System.Text.Json;
using k8s;
using k8s.Autorest;
using Release.Core.Verification;

namespace Release.Verifier;

public sealed class ClusterTargets
{
    public string KustomizationNamespace { get; set; } = "orders";

    public string DeploymentNamespace { get; set; } = "orders";

    public string DeploymentName { get; set; } = "orders-api";

    public string ContainerName { get; set; } = "orders-api";
}

/// <summary>
/// Reads Flux Kustomization and Argo Rollout status through the Kubernetes API. Its service account can
/// only get these two objects (ADR-0004); it never changes anything in the cluster.
/// </summary>
public sealed class KubernetesClusterObserver(IKubernetes kubernetes, ClusterTargets targets) : IClusterObserver
{
    public async Task<KustomizationStatus?> GetKustomizationAsync(string name, CancellationToken cancellationToken)
    {
        JsonElement obj;
        try
        {
            obj = await kubernetes.CustomObjects.GetNamespacedCustomObjectAsync<JsonElement>(
                "kustomize.toolkit.fluxcd.io", "v1", targets.KustomizationNamespace, "kustomizations", name,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!obj.TryGetProperty("status", out var status))
        {
            return new KustomizationStatus(false, "NoStatus", null, null, null);
        }

        JsonElement? ready = null;
        if (status.TryGetProperty("conditions", out var conditions))
        {
            foreach (var condition in conditions.EnumerateArray())
            {
                if (condition.GetProperty("type").GetString() == "Ready")
                {
                    ready = condition;
                }
            }
        }

        return new KustomizationStatus(
            ready?.GetProperty("status").GetString() == "True",
            ready is { } r && r.TryGetProperty("reason", out var reason) ? reason.GetString() : null,
            ready is { } m && m.TryGetProperty("message", out var message) ? message.GetString() : null,
            status.TryGetProperty("lastAppliedRevision", out var applied) ? applied.GetString() : null,
            status.TryGetProperty("lastAttemptedRevision", out var attempted) ? attempted.GetString() : null);
    }

    public async Task<RolloutStatus?> GetRolloutAsync(CancellationToken cancellationToken)
    {
        JsonElement obj;
        try
        {
            obj = await kubernetes.CustomObjects.GetNamespacedCustomObjectAsync<JsonElement>(
                "argoproj.io", "v1alpha1", targets.DeploymentNamespace, "rollouts", targets.DeploymentName,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return ParseRollout(obj, targets.ContainerName);
    }

    /// <summary>Maps an Argo Rollout object (ADR-0009) to what the verifier needs.</summary>
    public static RolloutStatus ParseRollout(JsonElement obj, string containerName)
    {
        var spec = obj.GetProperty("spec");
        string? image = null;
        if (spec.TryGetProperty("template", out var template)
            && template.TryGetProperty("spec", out var podSpec)
            && podSpec.TryGetProperty("containers", out var containers))
        {
            foreach (var container in containers.EnumerateArray())
            {
                if (container.GetProperty("name").GetString() == containerName)
                {
                    image = container.GetProperty("image").GetString();
                }
            }
        }

        var generation = obj.GetProperty("metadata").TryGetProperty("generation", out var g) ? g.GetInt64() : 0;
        var desired = spec.TryGetProperty("replicas", out var r) ? r.GetInt32() : 1;
        if (!obj.TryGetProperty("status", out var status))
        {
            return new RolloutStatus(image, generation, 0, desired, 0, 0, 0, false, Phase: "Progressing");
        }

        // Argo Rollouts reports observedGeneration as a string.
        var observed = status.TryGetProperty("observedGeneration", out var o)
            ? o.ValueKind == JsonValueKind.Number ? o.GetInt64() : long.TryParse(o.GetString(), out var parsed) ? parsed : 0
            : 0;
        var deadlineExceeded = false;
        if (status.TryGetProperty("conditions", out var conditions))
        {
            foreach (var condition in conditions.EnumerateArray())
            {
                if (condition.GetProperty("type").GetString() == "Progressing"
                    && condition.TryGetProperty("reason", out var reason)
                    && reason.GetString() == "ProgressDeadlineExceeded")
                {
                    deadlineExceeded = true;
                }
            }
        }

        return new RolloutStatus(
            image,
            generation,
            observed,
            desired,
            Int(status, "updatedReplicas"),
            Int(status, "readyReplicas"),
            Int(status, "availableReplicas"),
            deadlineExceeded,
            status.TryGetProperty("phase", out var phase) ? phase.GetString() : null,
            status.TryGetProperty("abort", out var abort) && abort.ValueKind == JsonValueKind.True,
            status.TryGetProperty("message", out var message) ? message.GetString() : null);
    }

    private static int Int(JsonElement status, string name) =>
        status.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
}
