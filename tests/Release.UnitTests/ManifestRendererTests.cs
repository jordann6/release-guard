using System.Text.Json;
using Release.Core.Artifacts;
using Release.Core.Deployments;

namespace Release.UnitTests;

public sealed class ManifestRendererTests : IDisposable
{
    private readonly string output = Path.Combine(Path.GetTempPath(), $"render-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(output))
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void Renders_the_repository_template_pinned_by_digest()
    {
        var deployment = new DeploymentRecord(
            "prod", 7, DeploymentKind.Normal, new string('a', 40),
            "123456789012.dkr.ecr.us-east-1.amazonaws.com/release-guard/app",
            DeploymentGuardTests.Digest(5),
            new ScanEvidence(DeploymentGuardTests.Digest(5), "pass", new string('f', 64)),
            null, 3, null, "run", DateTimeOffset.UnixEpoch);

        ManifestRenderer.Render(Path.Combine(RepositoryRoot(), "deploy", "release"), output, deployment);

        var app = Yaml(Path.Combine(output, "app", "kustomization.yaml"));
        Assert.Contains($"newName: {deployment.ImageRepository}", app, StringComparison.Ordinal);
        Assert.Contains($"digest: {deployment.ImageDigest}", app, StringComparison.Ordinal);
        Assert.Contains("release-guard/seq: \"7\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain("nameSuffix", app, StringComparison.Ordinal);

        var migrate = Yaml(Path.Combine(output, "migrate", "kustomization.yaml"));
        Assert.Contains("nameSuffix: -7", migrate, StringComparison.Ordinal);
        Assert.Contains($"digest: {deployment.ImageDigest}", migrate, StringComparison.Ordinal);

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "release.json")));
        Assert.Equal("1.0.7", manifest.RootElement.GetProperty("tag").GetString());
        Assert.Equal(deployment.ImageReference, manifest.RootElement.GetProperty("image").GetString());
    }

    [Fact]
    public void Template_does_not_set_what_the_renderer_appends()
    {
        foreach (var part in new[] { "app", "migrate" })
        {
            var text = Yaml(Path.Combine(RepositoryRoot(), "deploy", "release", part, "kustomization.yaml"));
            Assert.DoesNotContain("images:", text, StringComparison.Ordinal);
            Assert.DoesNotContain("nameSuffix", text, StringComparison.Ordinal);
            Assert.DoesNotContain("commonAnnotations", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Migrate_service_account_is_never_renamed_by_the_release_suffix()
    {
        // The IRSA trust policy accepts exactly system:serviceaccount:orders:orders-migrator
        // (ADR-0004). A ServiceAccount in the suffixed migrate Kustomization becomes
        // orders-migrator-<seq> and STS refuses it, so the account belongs to the platform layer.
        var root = RepositoryRoot();
        var migrate = Path.Combine(root, "deploy", "release", "migrate");
        Assert.DoesNotContain("kind: ServiceAccount", string.Join('\n', Directory.GetFiles(migrate).Select(File.ReadAllText)), StringComparison.Ordinal);
        Assert.Contains("serviceAccountName: orders-migrator", Yaml(Path.Combine(migrate, "job.yaml")), StringComparison.Ordinal);

        var platform = Yaml(Path.Combine(root, "deploy", "platform", "base", "migrator.yaml"));
        Assert.Contains("name: orders-migrator", platform, StringComparison.Ordinal);
        Assert.Contains("namespace: orders", platform, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("java")]
    [InlineData("python")]
    [InlineData("dotnet")]
    [InlineData("nodejs")]
    public void App_pods_opt_out_of_addon_auto_instrumentation(string language)
    {
        // The EKS observability add-on injects init containers that violate the restricted
        // PodSecurity profile of the orders namespace, so no pod of the release can start.
        var deployment = Yaml(Path.Combine(RepositoryRoot(), "deploy", "release", "app", "deployment.yaml"));
        Assert.Contains($"instrumentation.opentelemetry.io/inject-{language}: \"false\"", deployment, StringComparison.Ordinal);
        Assert.Contains($"cloudwatch.aws.amazon.com/auto-annotate-{language}: \"false\"", deployment, StringComparison.Ordinal);
    }

    /// <summary>The file without comment lines.</summary>
    private static string Yaml(string path) =>
        string.Join('\n', File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith('#')));

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "release-guard.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
