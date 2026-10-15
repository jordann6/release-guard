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
/// Reads Flux and Deployment status through the Kubernetes API. Its service account can
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
        k8s.Models.V1Deployment deployment;
        try
        {
            deployment = await kubernetes.AppsV1.ReadNamespacedDeploymentAsync(
                targets.DeploymentName, targets.DeploymentNamespace, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var image = deployment.Spec.Template.Spec.Containers.FirstOrDefault(c => c.Name == targets.ContainerName)?.Image;
        var status = deployment.Status;
        var deadlineExceeded = status?.Conditions?.Any(c => c.Type == "Progressing" && c.Reason == "ProgressDeadlineExceeded") == true;
        return new RolloutStatus(
            image,
            deployment.Metadata.Generation ?? 0,
            status?.ObservedGeneration ?? 0,
            deployment.Spec.Replicas ?? 1,
            status?.UpdatedReplicas ?? 0,
            status?.ReadyReplicas ?? 0,
            status?.AvailableReplicas ?? 0,
            deadlineExceeded);
    }
}
