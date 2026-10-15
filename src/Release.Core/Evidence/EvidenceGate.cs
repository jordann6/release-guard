using System.Text.Json;

namespace Release.Core.Evidence;

public sealed record PullRequestInfo(int Number, string HeadSha, string? MergeCommitSha, DateTimeOffset? MergedAt, string BaseRef);

public sealed record CheckRunInfo(long Id, string Name, string Status, string? Conclusion, string AppSlug, long CheckSuiteId);

public sealed record WorkflowRunInfo(long Id, string Path, string HeadSha, string Event, string? Conclusion, long CheckSuiteId, DateTimeOffset CreatedAt);

/// <summary>The few GitHub API reads the evidence gate needs. All are read-only.</summary>
public interface IGitHubEvidenceSource
{
    Task<IReadOnlyList<PullRequestInfo>> GetPullRequestsForCommitAsync(string sha, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkflowRunInfo>> GetWorkflowRunsAsync(string headSha, CancellationToken cancellationToken);

    Task<IReadOnlyList<CheckRunInfo>> GetCheckRunsAsync(string sha, CancellationToken cancellationToken);

    Task<string?> DownloadArtifactFileAsync(long runId, string artifactName, string fileName, CancellationToken cancellationToken);
}

/// <summary>pr-evidence.json, docs/schemas/pr-evidence.schema.json.</summary>
public sealed record PrEvidence
{
    public int SchemaVersion { get; init; }

    public string Source { get; init; } = string.Empty;

    public int? PrNumber { get; init; }

    public string HeadSha { get; init; } = string.Empty;

    public string? BaseSha { get; init; }

    public string? MergeRefSha { get; init; }

    public string MergeTree { get; init; } = string.Empty;

    public long RunId { get; init; }

    public int RunAttempt { get; init; }

    public Dictionary<string, string> Jobs { get; init; } = [];

    public PrImageEvidence Image { get; init; } = new();
}

public sealed record PrImageEvidence
{
    public string Digest { get; init; } = string.Empty;

    public string TrivyResult { get; init; } = string.Empty;

    public string TrivyReportSha256 { get; init; } = string.Empty;

    public int High { get; init; }

    public int Critical { get; init; }
}

public sealed record EvidenceGateOptions
{
    public string BaseBranch { get; init; } = "main";

    public string PrWorkflowPath { get; init; } = ".github/workflows/pr.yml";

    public string VerifyMainWorkflowPath { get; init; } = ".github/workflows/verify-main.yml";

    public string ArtifactName { get; init; } = "pr-evidence";

    public string ArtifactFile { get; init; } = "pr-evidence.json";

    /// <summary>Check runs that must have succeeded, as named under the caller job.</summary>
    public IReadOnlyList<string> RequiredChecks { get; init; } =
    [
        "checks / guardrails / ci / Secret scan (gitleaks)",
        "checks / format",
        "checks / build",
        "checks / unit-tests",
        "checks / integration-tests",
        "checks / migration-safety",
        "checks / n-minus-1",
        "checks / sast",
        "checks / image-scan",
        "checks / evidence",
    ];

    /// <summary>GitHub links a squash commit to its PR a few seconds after the merge.</summary>
    public int PullLookupAttempts { get; init; } = 6;

    public TimeSpan PullLookupDelay { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed record EvidenceResult(bool Passed, IReadOnlyList<string> Findings, PrEvidence? Evidence, string? Path)
{
    public static EvidenceResult Fail(IReadOnlyList<string> findings) => new(false, findings, null, null);
}

/// <summary>
/// Proves that the commit being released is exactly the tree that passed the PR checks
/// (ADR-0002). Pass or fail comes from the check-runs API, which a PR cannot write; the
/// evidence artifact supplies only the tested tree. Any gap fails closed.
/// </summary>
public sealed class EvidenceGate(IGitHubEvidenceSource github, TimeProvider clock, EvidenceGateOptions options)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<EvidenceResult> EvaluateAsync(string commitSha, string commitTree, CancellationToken cancellationToken)
    {
        var findings = new List<string>();

        var pull = await FindMergedPullAsync(commitSha, findings, cancellationToken).ConfigureAwait(false);
        if (pull is not null)
        {
            var viaPr = await EvaluateRunAsync(
                pull.HeadSha, options.PrWorkflowPath, "pull_request", commitTree,
                e => e.PrNumber == pull.Number && e.HeadSha == pull.HeadSha,
                $"PR #{pull.Number}", findings, cancellationToken).ConfigureAwait(false);
            if (viaPr is not null)
            {
                return new EvidenceResult(true, findings, viaPr, $"pull request #{pull.Number}");
            }
        }

        // Recovery path: the full suite was rerun against this commit on main.
        var viaMain = await EvaluateRunAsync(
            commitSha, options.VerifyMainWorkflowPath, "verify-main", commitTree,
            e => e.HeadSha == commitSha,
            "verify-main", findings, cancellationToken).ConfigureAwait(false);
        return viaMain is not null
            ? new EvidenceResult(true, findings, viaMain, "verify-main")
            : EvidenceResult.Fail(findings);
    }

    private async Task<PullRequestInfo?> FindMergedPullAsync(string commitSha, List<string> findings, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var pulls = (await github.GetPullRequestsForCommitAsync(commitSha, cancellationToken).ConfigureAwait(false))
                .Where(p => p.MergedAt is not null
                    && string.Equals(p.MergeCommitSha, commitSha, StringComparison.Ordinal)
                    && string.Equals(p.BaseRef, options.BaseBranch, StringComparison.Ordinal))
                .ToList();

            if (pulls.Count == 1)
            {
                return pulls[0];
            }

            if (pulls.Count > 1)
            {
                findings.Add($"Commit {commitSha} is the merge commit of {pulls.Count} PRs; refusing to choose.");
                return null;
            }

            if (attempt >= options.PullLookupAttempts)
            {
                findings.Add($"No merged PR into {options.BaseBranch} has {commitSha} as its merge commit.");
                return null;
            }

            await Task.Delay(options.PullLookupDelay, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PrEvidence?> EvaluateRunAsync(
        string runHeadSha,
        string workflowPath,
        string expectedSource,
        string commitTree,
        Func<PrEvidence, bool> identityMatches,
        string label,
        List<string> findings,
        CancellationToken cancellationToken)
    {
        var run = (await github.GetWorkflowRunsAsync(runHeadSha, cancellationToken).ConfigureAwait(false))
            .Where(r => string.Equals(r.Path, workflowPath, StringComparison.Ordinal)
                && string.Equals(r.Conclusion, "success", StringComparison.Ordinal))
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefault();
        if (run is null)
        {
            findings.Add($"{label}: no successful {workflowPath} run for {runHeadSha}.");
            return null;
        }

        // Required checks must come from GitHub Actions and from this very run.
        var checks = (await github.GetCheckRunsAsync(runHeadSha, cancellationToken).ConfigureAwait(false))
            .Where(c => c.AppSlug == "github-actions" && c.CheckSuiteId == run.CheckSuiteId)
            .GroupBy(c => c.Name)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Id).First(), StringComparer.Ordinal);
        var missing = options.RequiredChecks
            .Where(name => !checks.TryGetValue(name, out var c) || c.Status != "completed" || c.Conclusion != "success")
            .ToList();
        if (missing.Count > 0)
        {
            findings.Add($"{label}: required checks not successful in run {run.Id}: {string.Join(", ", missing)}.");
            return null;
        }

        var json = await github.DownloadArtifactFileAsync(run.Id, options.ArtifactName, options.ArtifactFile, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            findings.Add($"{label}: run {run.Id} has no {options.ArtifactName} artifact.");
            return null;
        }

        PrEvidence? evidence;
        try
        {
            evidence = JsonSerializer.Deserialize<PrEvidence>(json, Json);
        }
        catch (JsonException ex)
        {
            findings.Add($"{label}: evidence is not valid JSON: {ex.Message}");
            return null;
        }

        var problems = new List<string>();
        if (evidence is null || evidence.SchemaVersion != 1)
        {
            problems.Add("unsupported evidence schema");
        }
        else
        {
            if (evidence.Source != expectedSource)
            {
                problems.Add($"source is {evidence.Source}, expected {expectedSource}");
            }

            if (evidence.RunId != run.Id)
            {
                problems.Add($"evidence names run {evidence.RunId}, artifact came from run {run.Id}");
            }

            if (!identityMatches(evidence))
            {
                problems.Add("evidence names a different PR or head commit");
            }

            if (!string.Equals(evidence.MergeTree, commitTree, StringComparison.Ordinal))
            {
                problems.Add($"tested tree {evidence.MergeTree} differs from the released tree {commitTree}");
            }

            var failedJobs = evidence.Jobs.Where(j => j.Value != "success").Select(j => j.Key).ToList();
            if (failedJobs.Count > 0 || evidence.Jobs.Count == 0)
            {
                problems.Add($"jobs not successful: {string.Join(", ", failedJobs)}");
            }

            if (evidence.Image.TrivyResult != "pass")
            {
                problems.Add("PR image scan did not pass");
            }
        }

        if (problems.Count > 0)
        {
            findings.Add($"{label}: {string.Join("; ", problems)}.");
            return null;
        }

        return evidence;
    }
}
