using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Release.Core.Evidence;

/// <summary>
/// Read-only GitHub REST client for the evidence gate. Uses the workflow's GITHUB_TOKEN
/// with contents, checks and actions read permissions; never writes.
/// </summary>
public sealed class GitHubRestEvidenceSource : IGitHubEvidenceSource
{
    private const int PageSize = 100;
    private readonly HttpClient http;
    private readonly string repository;

    public GitHubRestEvidenceSource(HttpClient http, string repository, string? token)
    {
        ArgumentNullException.ThrowIfNull(http);
        this.http = http;
        this.repository = repository;
        http.BaseAddress ??= new Uri("https://api.github.com/");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("release-guard", "1"));
        if (!string.IsNullOrEmpty(token))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async Task<IReadOnlyList<PullRequestInfo>> GetPullRequestsForCommitAsync(string sha, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync($"repos/{repository}/commits/{sha}/pulls", cancellationToken).ConfigureAwait(false);
        return [.. doc.RootElement.EnumerateArray().Select(p => new PullRequestInfo(
            p.GetProperty("number").GetInt32(),
            p.GetProperty("head").GetProperty("sha").GetString()!,
            StringOrNull(p, "merge_commit_sha"),
            p.TryGetProperty("merged_at", out var m) && m.ValueKind == JsonValueKind.String ? m.GetDateTimeOffset() : null,
            p.GetProperty("base").GetProperty("ref").GetString()!))];
    }

    public async Task<IReadOnlyList<WorkflowRunInfo>> GetWorkflowRunsAsync(string headSha, CancellationToken cancellationToken)
    {
        var runs = new List<WorkflowRunInfo>();
        await foreach (var run in PagesAsync($"repos/{repository}/actions/runs?head_sha={headSha}", "workflow_runs", cancellationToken).ConfigureAwait(false))
        {
            runs.Add(new WorkflowRunInfo(
                run.GetProperty("id").GetInt64(),
                run.GetProperty("path").GetString()!,
                run.GetProperty("head_sha").GetString()!,
                run.GetProperty("event").GetString()!,
                StringOrNull(run, "conclusion"),
                run.GetProperty("check_suite_id").GetInt64(),
                run.GetProperty("created_at").GetDateTimeOffset()));
        }

        return runs;
    }

    public async Task<IReadOnlyList<CheckRunInfo>> GetCheckRunsAsync(string sha, CancellationToken cancellationToken)
    {
        var checks = new List<CheckRunInfo>();
        await foreach (var c in PagesAsync($"repos/{repository}/commits/{sha}/check-runs?filter=all", "check_runs", cancellationToken).ConfigureAwait(false))
        {
            checks.Add(new CheckRunInfo(
                c.GetProperty("id").GetInt64(),
                c.GetProperty("name").GetString()!,
                c.GetProperty("status").GetString()!,
                StringOrNull(c, "conclusion"),
                c.GetProperty("app").GetProperty("slug").GetString()!,
                c.GetProperty("check_suite").GetProperty("id").GetInt64()));
        }

        return checks;
    }

    public async Task<string?> DownloadArtifactFileAsync(long runId, string artifactName, string fileName, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync($"repos/{repository}/actions/runs/{runId}/artifacts?name={Uri.EscapeDataString(artifactName)}", cancellationToken).ConfigureAwait(false);
        var artifact = doc.RootElement.GetProperty("artifacts").EnumerateArray()
            .Where(a => a.GetProperty("name").GetString() == artifactName && !a.GetProperty("expired").GetBoolean())
            .Select(a => a.GetProperty("archive_download_url").GetString())
            .FirstOrDefault();
        if (artifact is null)
        {
            return null;
        }

        using var response = await http.GetAsync(new Uri(artifact), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entry = zip.GetEntry(fileName);
        if (entry is null)
        {
            return null;
        }

        using var reader = new StreamReader(entry.Open());
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri(path, UriKind.Relative), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<JsonElement> PagesAsync(
        string path,
        string arrayProperty,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        for (var page = 1; ; page++)
        {
            using var doc = await GetJsonAsync($"{path}{separator}per_page={PageSize}&page={page}", cancellationToken).ConfigureAwait(false);
            var items = doc.RootElement.GetProperty(arrayProperty).EnumerateArray().Select(e => e.Clone()).ToList();
            foreach (var item in items)
            {
                yield return item;
            }

            if (items.Count < PageSize)
            {
                yield break;
            }
        }
    }

    private static string? StringOrNull(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
