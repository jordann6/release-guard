using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Release.Core.Deployments;

namespace Release.Core.Artifacts;

/// <summary>Pushes and pulls deployment artifacts in the OCI deployment repository (ADR-0003).</summary>
public interface IArtifactPublisher
{
    /// <summary>Pushes <paramref name="directory"/> as <c>repository:tag</c> and returns its digest.</summary>
    Task<string> PushAsync(string directory, string tag, string sourceUrl, string revision, CancellationToken cancellationToken);

    /// <summary>Extracts <c>repository:tag</c> into <paramref name="directory"/>.</summary>
    Task PullAsync(string tag, string directory, CancellationToken cancellationToken);
}

public sealed record FluxCliOptions
{
    public required string Repository { get; init; }

    /// <summary>generic for a local registry, aws for ECR (uses the ambient AWS credentials).</summary>
    public string Provider { get; init; } = "generic";

    public bool InsecureRegistry { get; init; }

    public string FluxPath { get; init; } = "flux";
}

/// <summary>Uses the Flux CLI, so artifacts are exactly what Flux's source-controller expects.</summary>
public sealed class FluxCliArtifactPublisher(FluxCliOptions options) : IArtifactPublisher
{
    public async Task<string> PushAsync(string directory, string tag, string sourceUrl, string revision, CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "push", "artifact", $"oci://{options.Repository}:{tag}",
            "--path", directory,
            "--source", sourceUrl,
            "--revision", revision,
            "--provider", options.Provider,
            "--reproducible",
            "--output", "json",
        };
        if (options.InsecureRegistry)
        {
            args.Add("--insecure-registry");
        }

        var stdout = await RunAsync(args, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(stdout);
        var digest = doc.RootElement.GetProperty("digest").GetString();
        return DeploymentGuard.IsDigest(digest)
            ? digest!
            : throw new InvalidOperationException($"flux push returned no digest: {stdout}");
    }

    public async Task PullAsync(string tag, string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var args = new List<string>
        {
            "pull", "artifact", $"oci://{options.Repository}:{tag}",
            "--output", directory,
            "--provider", options.Provider,
        };
        if (options.InsecureRegistry)
        {
            args.Add("--insecure-registry");
        }

        await RunAsync(args, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> RunAsync(IEnumerable<string> args, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(options.FluxPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start flux.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"flux {string.Join(' ', start.ArgumentList)} failed: {await stderr.ConfigureAwait(false)}");
        }

        return await stdout.ConfigureAwait(false);
    }
}

/// <summary>
/// Renders the release template (deploy/release) for one release: pins the image by
/// digest, makes the migration Job name unique per release, and records what the
/// artifact contains. The template's kustomization files must not set images or nameSuffix.
/// </summary>
public static class ManifestRenderer
{
    public const string ImageName = "orders-api";

    internal static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Render(string templateDirectory, string outputDirectory, DeploymentRecord deployment)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        if (!Directory.Exists(Path.Combine(templateDirectory, "app")) || !Directory.Exists(Path.Combine(templateDirectory, "migrate")))
        {
            throw new DirectoryNotFoundException($"{templateDirectory} must contain app/ and migrate/.");
        }

        if (Directory.Exists(outputDirectory))
        {
            Directory.Delete(outputDirectory, recursive: true);
        }

        CopyDirectory(templateDirectory, outputDirectory);

        var seq = deployment.Seq.ToString(CultureInfo.InvariantCulture);
        var images = $"""

            images:
              - name: {ImageName}
                newName: {deployment.ImageRepository}
                digest: {deployment.ImageDigest}
            commonAnnotations:
              release-guard/seq: "{seq}"
              release-guard/commit: "{deployment.SourceCommit}"
            """;

        File.AppendAllText(Path.Combine(outputDirectory, "app", "kustomization.yaml"), images, Encoding.UTF8);
        File.AppendAllText(
            Path.Combine(outputDirectory, "migrate", "kustomization.yaml"),
            images + $"\nnameSuffix: -{seq}\n",
            Encoding.UTF8);

        var manifest = new
        {
            seq = deployment.Seq,
            tag = deployment.ArtifactTag,
            kind = deployment.Kind.ToString(),
            source_commit = deployment.SourceCommit,
            image = deployment.ImageReference,
            scan_report_sha256 = deployment.Scan.ReportSha256,
            restores_seq = deployment.RestoresSeq,
        };
        File.WriteAllText(
            Path.Combine(outputDirectory, "release.json"),
            JsonSerializer.Serialize(manifest, Indented),
            Encoding.UTF8);

        return outputDirectory;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }
}
