using System.Text.Json.Nodes;
using Release.Core.Contracts;

namespace Release.UnitTests;

public class DeploymentContractTests
{
    private const string Account = "123456789012";

    internal static JsonObject Valid() => new()
    {
        ["contract_version"] = "1",
        ["generation_id"] = "0123456789abcdef",
        ["region"] = "us-east-1",
        ["cluster_name"] = "prod",
        ["namespace"] = "orders",
        ["app_repository_url"] = $"{Account}.dkr.ecr.us-east-1.amazonaws.com/release-guard/app",
        ["deploy_repository_url"] = $"{Account}.dkr.ecr.us-east-1.amazonaws.com/release-guard/deploy",
        ["publish_role_arn"] = $"arn:aws:iam::{Account}:role/release-guard-publish",
        ["promote_role_arn"] = $"arn:aws:iam::{Account}:role/release-guard-promote",
        ["app_role_arn"] = $"arn:aws:iam::{Account}:role/release-guard-orders-api",
        ["migrator_role_arn"] = $"arn:aws:iam::{Account}:role/release-guard-orders-migrator",
        ["verifier_role_arn"] = $"arn:aws:iam::{Account}:role/release-guard-verifier",
        ["flux_source_role_arn"] = $"arn:aws:iam::{Account}:role/release-guard-flux-source",
        ["deployment_table"] = "release-guard-deployments",
        ["source_commit"] = new string('a', 40),
        ["published_at"] = "2026-10-15T12:00:00Z",
    };

    [Fact]
    public void Valid_contract_parses()
    {
        var result = DeploymentContracts.Parse(Valid().ToJsonString());
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal("orders", result.Contract!.Namespace);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void Missing_or_unreadable_contract_is_rejected(string? json)
    {
        Assert.False(DeploymentContracts.Parse(json).IsValid);
    }

    [Theory]
    [InlineData("generation_id")]
    [InlineData("promote_role_arn")]
    [InlineData("deployment_table")]
    public void Missing_required_field_is_rejected(string field)
    {
        var json = Valid();
        json.Remove(field);
        Assert.False(DeploymentContracts.Parse(json.ToJsonString()).IsValid);
    }

    [Theory]
    [InlineData("contract_version", "2")]
    [InlineData("generation_id", "XYZ")]
    [InlineData("app_repository_url", "docker.io/library/nginx")]
    [InlineData("publish_role_arn", "arn:aws:iam::123:role/x")]
    [InlineData("source_commit", "main")]
    public void Malformed_field_is_rejected(string field, string value)
    {
        var json = Valid();
        json[field] = value;
        var result = DeploymentContracts.Parse(json.ToJsonString());
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Unknown_fields_are_rejected_because_the_allowlist_is_the_boundary()
    {
        var json = Valid();
        json["db_password"] = "x";
        Assert.False(DeploymentContracts.Parse(json.ToJsonString()).IsValid);
    }

    [Fact]
    public void Stale_generation_is_rejected()
    {
        var contract = DeploymentContracts.Parse(Valid().ToJsonString()).Contract!;

        Assert.Empty(DeploymentContracts.CheckLive(contract, ContractStage.Publish, "0123456789abcdef", null));
        Assert.Single(DeploymentContracts.CheckLive(contract, ContractStage.Publish, "fedcba9876543210", null));
        Assert.Single(DeploymentContracts.CheckLive(contract, ContractStage.Publish, null, null));
    }

    [Fact]
    public void Assumed_role_must_match_the_stage()
    {
        var contract = DeploymentContracts.Parse(Valid().ToJsonString()).Contract!;
        var publishSession = $"arn:aws:sts::{Account}:assumed-role/release-guard-publish/GitHubActions";

        Assert.Empty(DeploymentContracts.CheckLive(contract, ContractStage.Publish, "0123456789abcdef", publishSession));
        Assert.Single(DeploymentContracts.CheckLive(contract, ContractStage.Promote, "0123456789abcdef", publishSession));
    }

    [Theory]
    [InlineData("arn:aws:iam::123456789012:role/app", "arn:aws:sts::123456789012:assumed-role/app/s", true)]
    [InlineData("arn:aws:iam::123456789012:role/path/to/app", "arn:aws:sts::123456789012:assumed-role/app/s", true)]
    [InlineData("arn:aws:iam::123456789012:role/app", "arn:aws:sts::999999999999:assumed-role/app/s", false)]
    [InlineData("arn:aws:iam::123456789012:role/app", "arn:aws:sts::123456789012:assumed-role/app2/s", false)]
    [InlineData("arn:aws:iam::123456789012:role/app", "arn:aws:iam::123456789012:user/app", false)]
    public void Role_comparison(string roleArn, string callerArn, bool same)
    {
        Assert.Equal(same, DeploymentContracts.SameRole(roleArn, callerArn));
    }
}
