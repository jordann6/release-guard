using System.Text.Json;
using System.Text.RegularExpressions;

namespace Release.Core.Contracts;

/// <summary>
/// Allowlisted infrastructure outputs published to SSM after a delivery apply (ADR-0005,
/// docs/schemas/deployment-contract.v1.schema.json). Workflows read these instead of
/// Terraform state or duplicated resource names.
/// </summary>
public sealed record DeploymentContract
{
    public required string ContractVersion { get; init; }

    public required string GenerationId { get; init; }

    public required string Region { get; init; }

    public required string ClusterName { get; init; }

    public required string Namespace { get; init; }

    public required string AppRepositoryUrl { get; init; }

    public required string DeployRepositoryUrl { get; init; }

    public required string PublishRoleArn { get; init; }

    public required string PromoteRoleArn { get; init; }

    public required string AppRoleArn { get; init; }

    public required string MigratorRoleArn { get; init; }

    public required string VerifierRoleArn { get; init; }

    public required string FluxSourceRoleArn { get; init; }

    public required string DeploymentTable { get; init; }

    public required string SourceCommit { get; init; }

    public required DateTimeOffset PublishedAt { get; init; }
}

public enum ContractStage
{
    Publish,
    Promote,
}

public sealed record ContractResult(DeploymentContract? Contract, IReadOnlyList<string> Errors)
{
    public bool IsValid => Contract is not null && Errors.Count == 0;
}

public static partial class DeploymentContracts
{
    public const int SupportedMajorVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Parses and validates a contract document. Never throws on bad input.</summary>
    public static ContractResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Invalid("The deployment contract is missing.");
        }

        DeploymentContract? contract;
        try
        {
            contract = JsonSerializer.Deserialize<DeploymentContract>(json, Json);
        }
        catch (JsonException ex)
        {
            return Invalid($"The deployment contract is not valid: {ex.Message}");
        }

        if (contract is null)
        {
            return Invalid("The deployment contract is empty.");
        }

        var errors = new List<string>();
        var major = contract.ContractVersion.Split('.')[0];
        if (major != SupportedMajorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            errors.Add($"Contract version {contract.ContractVersion} is not supported by this build (expects {SupportedMajorVersion}.x).");
        }

        Check(errors, "generation_id", contract.GenerationId, GenerationPattern());
        Check(errors, "region", contract.Region, RegionPattern());
        Check(errors, "namespace", contract.Namespace, NamespacePattern());
        Check(errors, "app_repository_url", contract.AppRepositoryUrl, RepositoryPattern());
        Check(errors, "deploy_repository_url", contract.DeployRepositoryUrl, RepositoryPattern());
        Check(errors, "source_commit", contract.SourceCommit, CommitPattern());
        foreach (var (name, arn) in new[]
        {
            ("publish_role_arn", contract.PublishRoleArn),
            ("promote_role_arn", contract.PromoteRoleArn),
            ("app_role_arn", contract.AppRoleArn),
            ("migrator_role_arn", contract.MigratorRoleArn),
            ("verifier_role_arn", contract.VerifierRoleArn),
            ("flux_source_role_arn", contract.FluxSourceRoleArn),
        })
        {
            Check(errors, name, arn, RoleArnPattern());
        }

        if (string.IsNullOrWhiteSpace(contract.ClusterName))
        {
            errors.Add("cluster_name is empty.");
        }

        if (string.IsNullOrWhiteSpace(contract.DeploymentTable))
        {
            errors.Add("deployment_table is empty.");
        }

        return new ContractResult(contract, errors);
    }

    /// <summary>
    /// Checks a parsed contract against live facts: the generation tag on the deploy
    /// repository (stale contract) and the role the workflow actually assumed.
    /// </summary>
    public static IReadOnlyList<string> CheckLive(
        DeploymentContract contract,
        ContractStage stage,
        string? liveGeneration,
        string? callerArn)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var errors = new List<string>();

        if (liveGeneration is null)
        {
            errors.Add("The deployment repository has no DeliveryGeneration tag; the infrastructure the contract names may not exist.");
        }
        else if (!string.Equals(liveGeneration, contract.GenerationId, StringComparison.Ordinal))
        {
            errors.Add($"The contract describes generation {contract.GenerationId}, but the live infrastructure is generation {liveGeneration}. Re-publish the contract.");
        }

        if (callerArn is not null)
        {
            var expected = stage == ContractStage.Publish ? contract.PublishRoleArn : contract.PromoteRoleArn;
            if (!SameRole(expected, callerArn))
            {
                errors.Add($"This {stage} step assumed {callerArn}, but the contract names {expected}.");
            }
        }

        return errors;
    }

    /// <summary>
    /// Compares a role ARN with the STS caller ARN for an assumed-role session
    /// (arn:aws:sts::ACCOUNT:assumed-role/NAME/SESSION).
    /// </summary>
    public static bool SameRole(string roleArn, string callerArn)
    {
        var role = RoleArnPattern().Match(roleArn ?? string.Empty);
        var caller = AssumedRolePattern().Match(callerArn ?? string.Empty);
        if (!role.Success || !caller.Success)
        {
            return false;
        }

        var roleName = role.Groups["path"].Value.Split('/')[^1];
        return role.Groups["account"].Value == caller.Groups["account"].Value
            && roleName == caller.Groups["name"].Value;
    }

    private static ContractResult Invalid(string error) => new(null, [error]);

    private static void Check(List<string> errors, string name, string? value, Regex pattern)
    {
        if (string.IsNullOrWhiteSpace(value) || !pattern.IsMatch(value))
        {
            errors.Add($"{name} is missing or malformed: '{value}'.");
        }
    }

    [GeneratedRegex("^[a-f0-9]{16}$")]
    private static partial Regex GenerationPattern();

    [GeneratedRegex("^[a-z]{2}-[a-z]+-[0-9]$")]
    private static partial Regex RegionPattern();

    [GeneratedRegex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$")]
    private static partial Regex NamespacePattern();

    [GeneratedRegex(@"^[0-9]{12}\.dkr\.ecr\.[a-z0-9-]+\.amazonaws\.com/[a-z0-9._/-]+$")]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex("^[a-f0-9]{40}$")]
    private static partial Regex CommitPattern();

    [GeneratedRegex(@"^arn:aws:iam::(?<account>[0-9]{12}):role/(?<path>[A-Za-z0-9+=,.@_/-]+)$")]
    private static partial Regex RoleArnPattern();

    [GeneratedRegex(@"^arn:aws:sts::(?<account>[0-9]{12}):assumed-role/(?<name>[A-Za-z0-9+=,.@_-]+)/[^/]+$")]
    private static partial Regex AssumedRolePattern();
}
