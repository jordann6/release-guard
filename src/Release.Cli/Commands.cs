using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.ECR;
using Amazon.ECR.Model;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Release.Core.Artifacts;
using Release.Core.Contracts;
using Release.Core.Deployments;
using Release.Core.Evidence;
using Command = System.CommandLine.Command;

namespace Release.Cli;

internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int Blocked = 3;
    public const int VerificationFailed = 4;
}

internal static class Commands
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    // Options shared by every command that touches deployment state.
    private static readonly Option<string> Env = new("--env") { Description = "Deployment environment.", DefaultValueFactory = _ => "prod" };
    private static readonly Option<string> Table = new("--table") { Description = "DynamoDB deployment table.", Required = true };
    private static readonly Option<string?> DynamoEndpoint = new("--dynamo-endpoint") { Description = "DynamoDB endpoint for local runs (DynamoDB Local)." };
    private static readonly Option<string> Region = new("--region") { DefaultValueFactory = _ => Environment.GetEnvironmentVariable("AWS_REGION") ?? "us-east-1" };

    // Options shared by commands that publish a deployment artifact.
    private static readonly Option<string> DeployRepo = new("--deploy-repo") { Description = "OCI repository for deployment artifacts.", Required = true };
    private static readonly Option<string> Template = new("--template") { Description = "Release template directory (app/ and migrate/).", DefaultValueFactory = _ => "deploy/release" };
    private static readonly Option<string> SourceUrl = new("--source-url") { DefaultValueFactory = _ => $"https://github.com/{Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "jordann6/release-guard"}" };
    private static readonly Option<string> Provider = new("--provider") { Description = "generic or aws.", DefaultValueFactory = _ => "aws" };
    private static readonly Option<bool> InsecureRegistry = new("--insecure-registry") { Description = "Plain HTTP registry (local only)." };
    private static readonly Option<string> WorkDir = new("--work-dir") { DefaultValueFactory = _ => Path.Combine(Path.GetTempPath(), "releasectl") };
    private static readonly Option<string> RunUrl = new("--run-url") { DefaultValueFactory = _ => DefaultRunUrl() };
    private static readonly Option<TimeSpan> Lease = new("--lease") { Description = "How long the release holds the lock before a missing verdict counts as a timeout.", DefaultValueFactory = _ => new GuardOptions().Lease };

    public static IEnumerable<Command> All() =>
    [
        EvidenceGateCommand(),
        ContractCheckCommand(),
        PromoteCommand(),
        WaitCommand(),
        RollbackCommand(),
        OverrideCommand(),
        StatusCommand(),
        InitTableCommand(),
    ];

    private static Command EvidenceGateCommand()
    {
        var repo = new Option<string>("--repo") { DefaultValueFactory = _ => Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? string.Empty };
        var sha = new Option<string>("--sha") { Required = true, Description = "Commit being released." };
        var tree = new Option<string?>("--tree") { Description = "Its tree hash. Computed with git when omitted." };
        var output = new Option<string?>("--out") { Description = "Write the accepted evidence here." };
        var command = new Command("evidence-gate", "Fail unless the commit is exactly the tree that passed the required PR checks (ADR-0002).") { repo, sha, tree, output };
        command.SetAction(async (parse, ct) =>
        {
            var commit = parse.GetValue(sha)!;
            var commitTree = parse.GetValue(tree) ?? await GitAsync($"rev-parse {commit}^{{tree}}", ct);
            using var http = new HttpClient();
            var source = new GitHubRestEvidenceSource(http, parse.GetValue(repo)!, Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
            var result = await new EvidenceGate(source, TimeProvider.System, new EvidenceGateOptions()).EvaluateAsync(commit, commitTree, ct);

            foreach (var finding in result.Findings)
            {
                Console.Error.WriteLine($"evidence-gate: {finding}");
            }

            if (!result.Passed)
            {
                Summary($"### Evidence gate: blocked\n\n{string.Join('\n', result.Findings.Select(f => $"- {f}"))}\n\nRun `verify-main.yml` to regenerate evidence for this commit.");
                return ExitCodes.Blocked;
            }

            if (parse.GetValue(output) is { } path)
            {
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result.Evidence, Json), ct);
            }

            Output("evidence_path", result.Path!);
            Output("pr_number", result.Evidence!.PrNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            Output("tree", commitTree);
            Summary($"### Evidence gate: passed\n\nTree `{commitTree}` passed every required check via {result.Path}.");
            Console.WriteLine($"evidence-gate: {commit} tree {commitTree} matches {result.Path}");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command ContractCheckCommand()
    {
        var source = new Option<string>("--source") { Required = true, Description = "ssm:<parameter name> or file:<path>." };
        var stage = new Option<ContractStage>("--stage") { Required = true };
        var skipLive = new Option<bool>("--skip-live") { Description = "Skip the live generation and caller checks (local runs only)." };
        var command = new Command("contract-check", "Validate the deployment contract and export it as step outputs (ADR-0005).") { source, stage, skipLive, Region };
        command.SetAction(async (parse, ct) =>
        {
            var location = parse.GetValue(source)!;
            var region = RegionEndpoint.GetBySystemName(parse.GetValue(Region));
            string? json;
            if (location.StartsWith("file:", StringComparison.Ordinal))
            {
                var path = location["file:".Length..];
                json = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
            }
            else if (location.StartsWith("ssm:", StringComparison.Ordinal))
            {
                using var ssm = new AmazonSimpleSystemsManagementClient(region);
                try
                {
                    json = (await ssm.GetParameterAsync(new GetParameterRequest { Name = location["ssm:".Length..] }, ct)).Parameter.Value;
                }
                catch (ParameterNotFoundException)
                {
                    json = null;
                }
            }
            else
            {
                Console.Error.WriteLine("contract-check: --source must start with ssm: or file:");
                return 2;
            }

            var parsed = DeploymentContracts.Parse(json);
            var errors = parsed.Errors.ToList();
            if (parsed.Contract is { } contract && !parse.GetValue(skipLive))
            {
                errors.AddRange(DeploymentContracts.CheckLive(
                    contract,
                    parse.GetValue(stage),
                    await LiveGenerationAsync(contract, ct),
                    await CallerArnAsync(region, ct)));
            }

            if (errors.Count > 0 || parsed.Contract is null)
            {
                foreach (var error in errors)
                {
                    Console.Error.WriteLine($"contract-check: {error}");
                }

                Summary($"### Deployment contract rejected\n\n{string.Join('\n', errors.Select(e => $"- {e}"))}");
                return ExitCodes.Blocked;
            }

            var c = parsed.Contract;
            Output("region", c.Region);
            Output("cluster_name", c.ClusterName);
            Output("namespace", c.Namespace);
            Output("app_repository_url", c.AppRepositoryUrl);
            Output("deploy_repository_url", c.DeployRepositoryUrl);
            Output("deployment_table", c.DeploymentTable);
            Output("generation_id", c.GenerationId);
            Console.WriteLine($"contract-check: contract v{c.ContractVersion} generation {c.GenerationId} accepted for {parse.GetValue(stage)}");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command PromoteCommand()
    {
        var commit = new Option<string>("--commit") { Required = true };
        var imageRepo = new Option<string>("--image-repo") { Required = true };
        var imageDigest = new Option<string>("--image-digest") { Required = true };
        var scanReport = new Option<string>("--scan-report") { Required = true, Description = "Trivy JSON report for the image." };
        var scannedDigest = new Option<string>("--scanned-digest") { Required = true, Description = "Manifest digest of the image that was scanned." };
        var sbomDigest = new Option<string?>("--sbom-digest");
        var prEvidence = new Option<string?>("--pr-evidence") { Description = "Evidence accepted by evidence-gate." };
        var initialize = new Option<bool>("--initialize") { Description = "First deployment of the environment." };
        var useOverride = new Option<bool>("--override") { Description = "Consume the approved override for this digest." };
        var command = new Command("promote", "Start a release under the deployment guard and publish its artifact (ADR-0006).")
        {
            Env, Table, DynamoEndpoint, Region, DeployRepo, Template, SourceUrl, Provider, InsecureRegistry, WorkDir, RunUrl, Lease,
            commit, imageRepo, imageDigest, scanReport, scannedDigest, sbomDigest, prEvidence, initialize, useOverride,
        };
        command.SetAction(async (parse, ct) =>
        {
            var scan = await ScanEvidenceAsync(parse.GetValue(scanReport)!, parse.GetValue(scannedDigest)!, ct);
            var evidence = parse.GetValue(prEvidence) is { } path
                ? JsonSerializer.Deserialize<PrEvidence>(await File.ReadAllTextAsync(path, ct), Json)
                : null;

            var request = new PromoteRequest(
                parse.GetValue(Env)!,
                parse.GetValue(commit)!,
                parse.GetValue(imageRepo)!,
                parse.GetValue(imageDigest)!,
                scan,
                parse.GetValue(sbomDigest),
                evidence?.PrNumber,
                evidence?.MergeTree,
                parse.GetValue(RunUrl)!,
                parse.GetValue(initialize),
                parse.GetValue(useOverride));

            using var dynamo = Dynamo(parse);
            var result = await Pipeline(parse, dynamo).PromoteAsync(request, ct);
            return Report("promote", result);
        });
        return command;
    }

    private static Command WaitCommand()
    {
        var seq = new Option<long>("--seq") { Required = true };
        var timeout = new Option<TimeSpan>("--timeout") { DefaultValueFactory = _ => TimeSpan.FromMinutes(12) };
        var interval = new Option<TimeSpan>("--interval") { DefaultValueFactory = _ => TimeSpan.FromSeconds(10) };
        var command = new Command("wait", "Wait for the in-cluster verifier's verdict on a release.") { Env, Table, DynamoEndpoint, Region, seq, timeout, interval };
        command.SetAction(async (parse, ct) =>
        {
            using var dynamo = Dynamo(parse);
            var store = new DynamoDeploymentStore(dynamo, parse.GetValue(Table)!);
            var guard = new DeploymentGuard(store, TimeProvider.System, new GuardOptions());
            var result = await guard.WaitAsync(parse.GetValue(Env)!, parse.GetValue(seq), parse.GetValue(timeout), parse.GetValue(interval), ct);

            Console.WriteLine(JsonSerializer.Serialize(new { result.Lock.State, result.TimedOut, verdict = result.Verification }, Json));
            var verdict = result.Verification;
            Summary(result.TimedOut
                ? $"### Release {parse.GetValue(seq)}: no verdict yet\n\nThe environment is still `{result.Lock.State}`. The verifier keeps running; the next release step reconciles its verdict."
                : $"### Release {parse.GetValue(seq)}: {verdict?.Result.ToString() ?? "no verdict"}\n\n{verdict?.Reason}\n\nEnvironment is now `{result.Lock.State}`.\n\n{SmokeTable(verdict)}");

            return verdict?.Result == VerdictResult.Pass ? ExitCodes.Ok : ExitCodes.VerificationFailed;
        });
        return command;
    }

    private static Command RollbackCommand()
    {
        var command = new Command("rollback", "Restore the last verified release as a new, higher release.")
        {
            Env, Table, DynamoEndpoint, Region, DeployRepo, Template, SourceUrl, Provider, InsecureRegistry, WorkDir, RunUrl, Lease,
        };
        command.SetAction(async (parse, ct) =>
        {
            using var dynamo = Dynamo(parse);
            var result = await Pipeline(parse, dynamo).RollbackAsync(parse.GetValue(Env)!, parse.GetValue(RunUrl)!, ct);
            return Report("rollback", result);
        });
        return command;
    }

    private static Command OverrideCommand()
    {
        var digest = new Option<string>("--digest") { Required = true };
        var reason = new Option<string>("--reason") { Required = true };
        var hours = new Option<double>("--hours") { DefaultValueFactory = _ => 4 };
        var actor = new Option<string>("--actor") { DefaultValueFactory = _ => Environment.GetEnvironmentVariable("GITHUB_ACTOR") ?? Environment.UserName };
        var command = new Command("override", "Record an approved, expiring, single-use override for one image digest.")
        {
            Env, Table, DynamoEndpoint, Region, RunUrl, digest, reason, hours, actor,
        };
        command.SetAction(async (parse, ct) =>
        {
            using var dynamo = Dynamo(parse);
            var store = new DynamoDeploymentStore(dynamo, parse.GetValue(Table)!);
            var guard = new DeploymentGuard(store, TimeProvider.System, new GuardOptions());
            try
            {
                var grant = await guard.GrantOverrideAsync(
                    parse.GetValue(Env)!, parse.GetValue(digest)!, parse.GetValue(reason)!,
                    TimeSpan.FromHours(parse.GetValue(hours)), parse.GetValue(RunUrl)!, parse.GetValue(actor)!, ct);
                Console.WriteLine(JsonSerializer.Serialize(grant, Json));
                Summary($"### Override granted\n\n`{grant.ImageDigest}` until {grant.ExpiresAt:u} by {grant.Actor}.\n\n> {grant.Reason}");
                return ExitCodes.Ok;
            }
            catch (Exception ex) when (ex is ArgumentException or ConcurrentUpdateException)
            {
                Console.Error.WriteLine($"override: {ex.Message}");
                return ExitCodes.Blocked;
            }
        });
        return command;
    }

    private static Command StatusCommand()
    {
        var command = new Command("status", "Show the deployment lock, the release in flight or last settled, and its verdict.") { Env, Table, DynamoEndpoint, Region };
        command.SetAction(async (parse, ct) =>
        {
            using var dynamo = Dynamo(parse);
            var env = parse.GetValue(Env)!;
            var store = new DynamoDeploymentStore(dynamo, parse.GetValue(Table)!);
            var guard = new DeploymentGuard(store, TimeProvider.System, new GuardOptions());
            var current = await guard.ReconcileAsync(env, ct);
            if (current is null)
            {
                Console.WriteLine($"{env}: uninitialized");
                return ExitCodes.Ok;
            }

            var seq = current.InflightSeq ?? current.LastSeq;
            var deployment = await store.GetDeploymentAsync(env, seq, ct);
            var verdict = await store.GetVerificationAsync(env, seq, ct);
            DeploymentRecord? healthy = current.LastHealthySeq is { } h ? await store.GetDeploymentAsync(env, h, ct) : null;
            Console.WriteLine(JsonSerializer.Serialize(new { @lock = current, latest = deployment, verdict, last_healthy = healthy }, Json));
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command InitTableCommand()
    {
        var command = new Command("init-table", "Create the deployment table (local runs; in AWS the infrastructure layer owns it).") { Table, DynamoEndpoint, Region };
        command.SetAction(async (parse, ct) =>
        {
            using var dynamo = Dynamo(parse);
            await DynamoDeploymentStore.CreateTableAsync(dynamo, parse.GetValue(Table)!, ct);
            Console.WriteLine($"init-table: {parse.GetValue(Table)} ready");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static int Report(string verb, PipelineResult result)
    {
        var d = result.Decision;
        Console.WriteLine(JsonSerializer.Serialize(new { outcome = d.Outcome, reason = d.Reason, seq = d.Deployment?.Seq, tag = d.Deployment?.ArtifactTag, artifact_digest = result.ArtifactDigest, image = d.Deployment?.ImageReference }, Json));
        Output("outcome", d.Outcome.ToString());
        Output("seq", d.Deployment?.Seq.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        Output("artifact_digest", result.ArtifactDigest ?? string.Empty);
        Summary($"### {verb}: {d.Outcome}\n\n{d.Reason}" + (result.ArtifactDigest is { } a ? $"\n\nArtifact `{d.Deployment?.ArtifactTag}@{a}` for image `{d.Deployment?.ImageReference}`." : string.Empty));

        if (d.Outcome == GuardOutcome.Blocked)
        {
            Console.Error.WriteLine($"{verb}: blocked: {d.Reason}");
            return ExitCodes.Blocked;
        }

        return ExitCodes.Ok;
    }

    private static ReleasePipeline Pipeline(ParseResult parse, IAmazonDynamoDB dynamo)
    {
        var store = new DynamoDeploymentStore(dynamo, parse.GetValue(Table)!);
        var guard = new DeploymentGuard(store, TimeProvider.System, new GuardOptions { Lease = parse.GetValue(Lease) });
        var publisher = new FluxCliArtifactPublisher(new FluxCliOptions
        {
            Repository = parse.GetValue(DeployRepo)!,
            Provider = parse.GetValue(Provider)!,
            InsecureRegistry = parse.GetValue(InsecureRegistry),
        });
        return new ReleasePipeline(guard, store, publisher, new PublishSettings(parse.GetValue(Template)!, parse.GetValue(WorkDir)!, parse.GetValue(SourceUrl)!));
    }

    private static AmazonDynamoDBClient Dynamo(ParseResult parse)
    {
        var region = parse.GetValue(Region)!;
        if (parse.GetValue(DynamoEndpoint) is { } endpoint)
        {
            return new AmazonDynamoDBClient(
                new BasicAWSCredentials("local", "local"),
                new AmazonDynamoDBConfig { ServiceURL = endpoint, AuthenticationRegion = region });
        }

        return new AmazonDynamoDBClient(RegionEndpoint.GetBySystemName(region));
    }

    /// <summary>Scan evidence from a Trivy JSON report: pass only with zero fixable HIGH or CRITICAL findings.</summary>
    private static async Task<ScanEvidence> ScanEvidenceAsync(string reportPath, string scannedDigest, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(reportPath, ct);
        using var doc = JsonDocument.Parse(bytes);
        var blocking = 0;
        if (doc.RootElement.TryGetProperty("Results", out var results))
        {
            foreach (var r in results.EnumerateArray())
            {
                if (r.TryGetProperty("Vulnerabilities", out var vulns))
                {
                    blocking += vulns.EnumerateArray().Count(v => v.GetProperty("Severity").GetString() is "HIGH" or "CRITICAL");
                }
            }
        }

        return new ScanEvidence(scannedDigest, blocking == 0 ? "pass" : "fail", Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static async Task<string?> LiveGenerationAsync(DeploymentContract contract, CancellationToken ct)
    {
        // <account>.dkr.ecr.<region>.amazonaws.com/<name>
        var host = contract.DeployRepositoryUrl.Split('/')[0];
        var name = contract.DeployRepositoryUrl[(host.Length + 1)..];
        var account = host.Split('.')[0];
        using var ecr = new AmazonECRClient(RegionEndpoint.GetBySystemName(contract.Region));
        try
        {
            var tags = await ecr.ListTagsForResourceAsync(new Amazon.ECR.Model.ListTagsForResourceRequest
            {
                ResourceArn = $"arn:aws:ecr:{contract.Region}:{account}:repository/{name}",
            }, ct);
            return tags.Tags?.FirstOrDefault(t => t.Key == "DeliveryGeneration")?.Value;
        }
        catch (RepositoryNotFoundException)
        {
            return null;
        }
    }

    private static async Task<string> CallerArnAsync(RegionEndpoint region, CancellationToken ct)
    {
        using var sts = new AmazonSecurityTokenServiceClient(region);
        return (await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct)).Arn;
    }

    private static async Task<string> GitAsync(string arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo("git", arguments) { RedirectStandardOutput = true, UseShellExecute = false };
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {arguments} failed");
    }

    private static string DefaultRunUrl()
    {
        var server = Environment.GetEnvironmentVariable("GITHUB_SERVER_URL");
        var repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        var run = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
        return server is not null && repo is not null && run is not null ? $"{server}/{repo}/actions/runs/{run}" : $"local:{Environment.UserName}@{Environment.MachineName}";
    }

    private static string SmokeTable(VerificationRecord? verdict) =>
        verdict is null || verdict.Smoke.Count == 0
            ? string.Empty
            : "| Smoke step | Result |\n|---|---|\n" + string.Join('\n', verdict.Smoke.Select(s => $"| {s.Name} | {(s.Passed ? "pass" : "FAIL")}: {s.Detail} |"));

    private static void Output(string name, string value)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_OUTPUT") is { } path)
        {
            File.AppendAllText(path, $"{name}={value}\n");
        }
    }

    private static void Summary(string markdown)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { } path)
        {
            File.AppendAllText(path, markdown + "\n\n");
        }
    }
}
