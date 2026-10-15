using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Release.Core.Evidence;

namespace Release.UnitTests;

public class EvidenceGateTests
{
    private const string MergeSha = "a22fce9000000000000000000000000000000000";
    private const string HeadSha = "e7a20631f0d8340974d35d7fe127b720965ffc5c";
    private const string Tree = "205e6f897d351273ebbdce3c2d90698aed37e96b";
    private const long RunId = 38699368714;
    private const long SuiteId = 4242;

    private readonly FakeTimeProvider clock = new();
    private readonly FakeGitHub github = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly EvidenceGateOptions Options = new() { PullLookupAttempts = 3, PullLookupDelay = TimeSpan.Zero };

    public EvidenceGateTests()
    {
        github.Pulls[MergeSha] = [new PullRequestInfo(1, HeadSha, MergeSha, DateTimeOffset.UnixEpoch, "main")];
        github.Runs[HeadSha] = [new WorkflowRunInfo(RunId, ".github/workflows/pr.yml", HeadSha, "pull_request", "success", SuiteId, DateTimeOffset.UnixEpoch)];
        github.Checks[HeadSha] = [.. Options.RequiredChecks.Select((name, i) => new CheckRunInfo(i, name, "completed", "success", "github-actions", SuiteId))];
        github.Artifacts[RunId] = Evidence().ToJsonString();
    }

    private EvidenceGate Gate => new(github, clock, Options);

    [Fact]
    public async Task Matching_tree_with_all_checks_passes()
    {
        var result = await Gate.EvaluateAsync(MergeSha, Tree, Ct);
        Assert.True(result.Passed, string.Join("; ", result.Findings));
        Assert.Equal("pull request #1", result.Path);
        Assert.Equal(Tree, result.Evidence!.MergeTree);
    }

    [Fact]
    public async Task Different_tree_fails_closed()
    {
        var result = await Gate.EvaluateAsync(MergeSha, new string('9', 40), Ct);
        Assert.False(result.Passed);
        Assert.Contains(result.Findings, f => f.Contains("differs from the released tree", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Commit_with_no_merged_pr_fails_after_retrying()
    {
        github.Pulls.Clear();
        var result = await Gate.EvaluateAsync(MergeSha, Tree, Ct);

        Assert.False(result.Passed);
        Assert.Equal(3, github.PullLookups);
    }

    [Fact]
    public async Task Pr_link_that_appears_late_is_found_on_retry()
    {
        var pulls = github.Pulls[MergeSha];
        github.Pulls.Clear();
        github.OnPullLookup = n => { if (n == 2) { github.Pulls[MergeSha] = pulls; } };

        Assert.True((await Gate.EvaluateAsync(MergeSha, Tree, Ct)).Passed);
    }

    [Fact]
    public async Task Check_run_from_another_app_does_not_count()
    {
        github.Checks[HeadSha] = [.. github.Checks[HeadSha].Select(c => c.Name == "checks / sast" ? c with { AppSlug = "some-other-app" } : c)];

        var result = await Gate.EvaluateAsync(MergeSha, Tree, Ct);
        Assert.False(result.Passed);
        Assert.Contains(result.Findings, f => f.Contains("checks / sast", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Check_runs_from_a_different_run_do_not_count()
    {
        github.Checks[HeadSha] = [.. github.Checks[HeadSha].Select(c => c with { CheckSuiteId = 1 })];
        Assert.False((await Gate.EvaluateAsync(MergeSha, Tree, Ct)).Passed);
    }

    [Fact]
    public async Task Failed_required_check_fails()
    {
        github.Checks[HeadSha] = [.. github.Checks[HeadSha].Select(c => c.Name == "checks / image-scan" ? c with { Conclusion = "failure" } : c)];
        Assert.False((await Gate.EvaluateAsync(MergeSha, Tree, Ct)).Passed);
    }

    [Fact]
    public async Task Missing_artifact_fails()
    {
        github.Artifacts.Clear();
        var result = await Gate.EvaluateAsync(MergeSha, Tree, Ct);
        Assert.False(result.Passed);
        Assert.Contains(result.Findings, f => f.Contains("no pr-evidence artifact", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("pr_number", 7)]
    [InlineData("run_id", 1)]
    [InlineData("source", "verify-main")]
    public async Task Evidence_naming_something_else_fails(string field, object value)
    {
        var evidence = Evidence();
        evidence[field] = JsonValue.Create(value);
        github.Artifacts[RunId] = evidence.ToJsonString();

        Assert.False((await Gate.EvaluateAsync(MergeSha, Tree, Ct)).Passed);
    }

    [Fact]
    public async Task Verify_main_evidence_recovers_a_commit_without_pr_evidence()
    {
        github.Artifacts.Clear();
        const long mainRun = 99;
        github.Runs[MergeSha] = [new WorkflowRunInfo(mainRun, ".github/workflows/verify-main.yml", MergeSha, "workflow_dispatch", "success", 7, DateTimeOffset.UnixEpoch)];
        github.Checks[MergeSha] = [.. Options.RequiredChecks.Select((name, i) => new CheckRunInfo(100 + i, name, "completed", "success", "github-actions", 7))];
        var evidence = Evidence();
        evidence["source"] = "verify-main";
        evidence["run_id"] = mainRun;
        evidence["head_sha"] = MergeSha;
        evidence.Remove("pr_number");
        github.Artifacts[mainRun] = evidence.ToJsonString();

        var result = await Gate.EvaluateAsync(MergeSha, Tree, Ct);
        Assert.True(result.Passed, string.Join("; ", result.Findings));
        Assert.Equal("verify-main", result.Path);
    }

    private static JsonObject Evidence() => new()
    {
        ["schema_version"] = 1,
        ["source"] = "pull_request",
        ["pr_number"] = 1,
        ["head_sha"] = HeadSha,
        ["base_sha"] = "da1f71b1bd29b37f09b926047fdeae6a0e444872",
        ["merge_ref_sha"] = "e879e4ba70655298ae8481129c9859b2ab69fd52",
        ["merge_tree"] = Tree,
        ["run_id"] = RunId,
        ["run_attempt"] = 1,
        ["jobs"] = new JsonObject { ["build"] = "success", ["sast"] = "success", ["image-scan"] = "success" },
        ["image"] = new JsonObject
        {
            ["digest"] = "sha256:5f18a25a0959ce9e13f155a88fdf043f424951416076acca5a365cfa7b14ae79",
            ["trivy_result"] = "pass",
            ["trivy_report_sha256"] = "f49f1ed22c59d66f88f85387eba5979c3eed183cfc7bc7befe7265a8e573c89d",
            ["high"] = 0,
            ["critical"] = 0,
        },
    };

    private sealed class FakeGitHub : IGitHubEvidenceSource
    {
        public Dictionary<string, List<PullRequestInfo>> Pulls { get; } = [];

        public Dictionary<string, List<WorkflowRunInfo>> Runs { get; } = [];

        public Dictionary<string, List<CheckRunInfo>> Checks { get; } = [];

        public Dictionary<long, string> Artifacts { get; } = [];

        public int PullLookups { get; private set; }

        public Action<int>? OnPullLookup { get; set; }

        public Task<IReadOnlyList<PullRequestInfo>> GetPullRequestsForCommitAsync(string sha, CancellationToken cancellationToken)
        {
            PullLookups++;
            OnPullLookup?.Invoke(PullLookups);
            return Task.FromResult<IReadOnlyList<PullRequestInfo>>(Pulls.GetValueOrDefault(sha) ?? []);
        }

        public Task<IReadOnlyList<WorkflowRunInfo>> GetWorkflowRunsAsync(string headSha, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowRunInfo>>(Runs.GetValueOrDefault(headSha) ?? []);

        public Task<IReadOnlyList<CheckRunInfo>> GetCheckRunsAsync(string sha, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CheckRunInfo>>(Checks.GetValueOrDefault(sha) ?? []);

        public Task<string?> DownloadArtifactFileAsync(long runId, string artifactName, string fileName, CancellationToken cancellationToken) =>
            Task.FromResult(Artifacts.GetValueOrDefault(runId));
    }
}
