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
