using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Release.Core.Deployments;

/// <summary>
/// DynamoDB store (ADR-0006). Partition keys carry the item type and environment
/// (LOCK#prod, DEPLOY#prod#7, VERIFY#prod#7, OVERRIDE#prod#sha256:...) so IAM
/// dynamodb:LeadingKeys conditions separate what the promote role and the verifier
/// may write (ADR-0004). Every read is strongly consistent.
/// </summary>
public sealed class DynamoDeploymentStore(IAmazonDynamoDB dynamo, string tableName) : IDeploymentStore
{
    private const string Pk = "pk";
    private const string Sk = "sk";

    public static string LockKey(string environment) => $"LOCK#{environment}";

    public static string DeployKey(string environment, long seq) => $"DEPLOY#{environment}#{seq}";

    public static string VerifyKey(string environment, long seq) => $"VERIFY#{environment}#{seq}";

    public static string OverrideKey(string environment, string digest) => $"OVERRIDE#{environment}#{digest}";

    public async Task<LockRecord?> GetLockAsync(string environment, CancellationToken cancellationToken)
    {
        var item = await GetAsync(LockKey(environment), "STATE", cancellationToken).ConfigureAwait(false);
        return item is null ? null : ToLock(item);
    }

    public async Task<DeploymentRecord?> GetDeploymentAsync(string environment, long seq, CancellationToken cancellationToken)
    {
        var item = await GetAsync(DeployKey(environment, seq), "RECORD", cancellationToken).ConfigureAwait(false);
        return item is null ? null : ToDeployment(item);
    }

    public async Task<VerificationRecord?> GetVerificationAsync(string environment, long seq, CancellationToken cancellationToken)
    {
        var item = await GetAsync(VerifyKey(environment, seq), "RESULT", cancellationToken).ConfigureAwait(false);
        return item is null ? null : ToVerification(item);
    }

    public async Task<OverrideGrant?> GetOverrideAsync(string environment, string imageDigest, CancellationToken cancellationToken)
    {
        var item = await GetAsync(OverrideKey(environment, imageDigest), "GRANT", cancellationToken).ConfigureAwait(false);
        return item is null ? null : ToOverride(item);
    }

    public async Task<LockRecord> BeginDeploymentAsync(
        LockRecord? expected,
        DeploymentRecord deployment,
        EnvironmentState newState,
        string leaseOwner,
        DateTimeOffset leaseUntil,
        string? consumeOverrideDigest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        var env = deployment.Environment;
        var next = new LockRecord(
            env,
            newState,
            deployment.Seq,
            deployment.Seq,
            leaseOwner,
            leaseUntil,
            expected?.LastHealthySeq,
            (expected?.Version ?? 0) + 1);

        if (deployment.Seq != (expected?.LastSeq ?? 0) + 1)
        {
            throw new ConcurrentUpdateException($"Release {deployment.Seq} does not follow {expected?.LastSeq ?? 0}.");
        }

        var items = new List<TransactWriteItem>
        {
            new()
            {
                Put = new Put
                {
                    TableName = tableName,
                    Item = FromLock(next),
                    ConditionExpression = expected is null ? "attribute_not_exists(pk)" : "version = :v",
                    ExpressionAttributeValues = expected is null ? null : new() { [":v"] = N(expected.Version) },
                },
            },
            new()
            {
                Put = new Put
                {
                    TableName = tableName,
                    Item = FromDeployment(deployment),
                    ConditionExpression = "attribute_not_exists(pk)",
                },
            },
        };

        if (consumeOverrideDigest is not null)
        {
            items.Add(new TransactWriteItem
            {
                Update = new Update
                {
                    TableName = tableName,
                    Key = Key(OverrideKey(env, consumeOverrideDigest), "GRANT"),
                    UpdateExpression = "SET consumed_by_seq = :seq",
                    ConditionExpression = "attribute_exists(pk) AND attribute_not_exists(consumed_by_seq) AND expires_epoch > :now",
                    ExpressionAttributeValues = new()
                    {
                        [":seq"] = N(deployment.Seq),
                        [":now"] = N(deployment.CreatedAt.ToUnixTimeSeconds()),
                    },
                },
            });
        }

        try
        {
            await dynamo.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = items }, cancellationToken).ConfigureAwait(false);
        }
        catch (TransactionCanceledException ex)
        {
            throw new ConcurrentUpdateException(
                $"Release {deployment.Seq} lost a conditional write: {string.Join(", ", ex.CancellationReasons.Select(r => r.Code))}", ex);
        }

        return next;
    }

    public async Task SetArtifactDigestAsync(string environment, long seq, string artifactDigest, CancellationToken cancellationToken)
    {
        try
        {
            await dynamo.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = tableName,
                Key = Key(DeployKey(environment, seq), "RECORD"),
                UpdateExpression = "SET artifact_digest = :d",
                ConditionExpression = "attribute_exists(pk) AND (attribute_not_exists(artifact_digest) OR artifact_digest = :d)",
                ExpressionAttributeValues = new() { [":d"] = S(artifactDigest) },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException ex)
        {
            throw new ConcurrentUpdateException($"Release {seq} is missing or already has a different artifact.", ex);
        }
    }

    public async Task<LockRecord> SettleAsync(
        LockRecord expected,
        EnvironmentState newState,
        long? lastHealthySeq,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var next = expected with
        {
            State = newState,
            InflightSeq = null,
            LeaseOwner = null,
            LeaseUntil = null,
            LastHealthySeq = lastHealthySeq,
            Version = expected.Version + 1,
        };

        try
        {
            await dynamo.PutItemAsync(new PutItemRequest
            {
                TableName = tableName,
                Item = FromLock(next),
                ConditionExpression = "version = :v",
                ExpressionAttributeValues = new() { [":v"] = N(expected.Version) },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException ex)
        {
            throw new ConcurrentUpdateException($"Lock for {expected.Environment} changed.", ex);
        }

        return next;
    }

    public async Task PutVerificationAsync(VerificationRecord verification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verification);
        try
        {
            await dynamo.PutItemAsync(new PutItemRequest
            {
                TableName = tableName,
                Item = FromVerification(verification),
                ConditionExpression = "attribute_not_exists(pk)",
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException ex)
        {
            throw new ConcurrentUpdateException($"Release {verification.Seq} already has a verdict.", ex);
        }
    }

    public async Task PutOverrideAsync(OverrideGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        try
        {
            await dynamo.PutItemAsync(new PutItemRequest
            {
                TableName = tableName,
                Item = FromOverride(grant),
                // Replace only a grant that is gone, used or expired.
                ConditionExpression = "attribute_not_exists(pk) OR attribute_exists(consumed_by_seq) OR expires_epoch <= :now",
                ExpressionAttributeValues = new() { [":now"] = N(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException ex)
        {
            throw new ConcurrentUpdateException($"An unused override already exists for {grant.ImageDigest}.", ex);
        }
    }

    /// <summary>Creates the table for local runs. In AWS the infrastructure layer owns it.</summary>
    public static async Task CreateTableAsync(IAmazonDynamoDB dynamo, string tableName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dynamo);
        try
        {
            await dynamo.CreateTableAsync(new CreateTableRequest
            {
                TableName = tableName,
                BillingMode = BillingMode.PAY_PER_REQUEST,
                AttributeDefinitions = [new(Pk, ScalarAttributeType.S), new(Sk, ScalarAttributeType.S)],
                KeySchema = [new(Pk, KeyType.HASH), new(Sk, KeyType.RANGE)],
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ResourceInUseException)
        {
            // Already exists.
        }
    }

    private async Task<Dictionary<string, AttributeValue>?> GetAsync(string pk, string sk, CancellationToken cancellationToken)
    {
        var response = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = tableName,
            Key = Key(pk, sk),
            ConsistentRead = true,
        }, cancellationToken).ConfigureAwait(false);
        return response.Item is { Count: > 0 } item ? item : null;
    }

    private static Dictionary<string, AttributeValue> Key(string pk, string sk) => new()
    {
        [Pk] = S(pk),
        [Sk] = S(sk),
    };

    private static Dictionary<string, AttributeValue> FromLock(LockRecord l)
    {
        var item = Key(LockKey(l.Environment), "STATE");
        item["environment"] = S(l.Environment);
        item["state"] = S(l.State.ToString());
        item["last_seq"] = N(l.LastSeq);
        item["version"] = N(l.Version);
        SetIf(item, "inflight_seq", l.InflightSeq);
        SetIf(item, "lease_owner", l.LeaseOwner);
        SetIf(item, "lease_until", l.LeaseUntil);
        SetIf(item, "last_healthy_seq", l.LastHealthySeq);
        return item;
    }

    private static LockRecord ToLock(Dictionary<string, AttributeValue> i) => new(
        i["environment"].S,
        Enum.Parse<EnvironmentState>(i["state"].S),
        Long(i, "last_seq")!.Value,
        Long(i, "inflight_seq"),
        Str(i, "lease_owner"),
        Time(i, "lease_until"),
        Long(i, "last_healthy_seq"),
        Long(i, "version")!.Value);

    private static Dictionary<string, AttributeValue> FromDeployment(DeploymentRecord d)
    {
        var item = Key(DeployKey(d.Environment, d.Seq), "RECORD");
        item["environment"] = S(d.Environment);
        item["seq"] = N(d.Seq);
        item["kind"] = S(d.Kind.ToString());
        item["source_commit"] = S(d.SourceCommit);
        item["image_repository"] = S(d.ImageRepository);
        item["image_digest"] = S(d.ImageDigest);
        item["artifact_tag"] = S(d.ArtifactTag);
        item["scan_digest"] = S(d.Scan.ImageDigest);
        item["scan_result"] = S(d.Scan.Result);
        item["scan_report_sha256"] = S(d.Scan.ReportSha256);
        item["run_url"] = S(d.RunUrl);
        item["created_at"] = S(d.CreatedAt);
        SetIf(item, "sbom_digest", d.SbomDigest);
        SetIf(item, "pr_number", d.PrNumber);
        SetIf(item, "pr_tree", d.PrTree);
        SetIf(item, "restores_seq", d.RestoresSeq);
        SetIf(item, "artifact_digest", d.ArtifactDigest);
        return item;
    }

    private static DeploymentRecord ToDeployment(Dictionary<string, AttributeValue> i) => new(
        i["environment"].S,
        Long(i, "seq")!.Value,
        Enum.Parse<DeploymentKind>(i["kind"].S),
        i["source_commit"].S,
        i["image_repository"].S,
        i["image_digest"].S,
        new ScanEvidence(i["scan_digest"].S, i["scan_result"].S, i["scan_report_sha256"].S),
        Str(i, "sbom_digest"),
        (int?)Long(i, "pr_number"),
        Str(i, "pr_tree"),
        i["run_url"].S,
        Time(i, "created_at")!.Value,
        Long(i, "restores_seq"),
        Str(i, "artifact_digest"));

    private static Dictionary<string, AttributeValue> FromVerification(VerificationRecord v)
    {
        var item = Key(VerifyKey(v.Environment, v.Seq), "RESULT");
        item["environment"] = S(v.Environment);
        item["seq"] = N(v.Seq);
        item["result"] = S(v.Result.ToString());
        item["reason"] = S(v.Reason);
        item["started_at"] = S(v.StartedAt);
        item["finished_at"] = S(v.FinishedAt);
        item["smoke"] = S(JsonSerializer.Serialize(v.Smoke));
        SetIf(item, "observed_artifact_revision", v.ObservedArtifactRevision);
        SetIf(item, "observed_image", v.ObservedImage);
        return item;
    }

    private static VerificationRecord ToVerification(Dictionary<string, AttributeValue> i) => new(
        i["environment"].S,
        Long(i, "seq")!.Value,
        Enum.Parse<VerdictResult>(i["result"].S),
        Str(i, "observed_artifact_revision"),
        Str(i, "observed_image"),
        i["reason"].S,
        Time(i, "started_at")!.Value,
        Time(i, "finished_at")!.Value,
        JsonSerializer.Deserialize<List<SmokeStep>>(i["smoke"].S) ?? []);

    private static Dictionary<string, AttributeValue> FromOverride(OverrideGrant g)
    {
        var item = Key(OverrideKey(g.Environment, g.ImageDigest), "GRANT");
        item["environment"] = S(g.Environment);
        item["image_digest"] = S(g.ImageDigest);
        item["reason"] = S(g.Reason);
        item["expires_at"] = S(g.ExpiresAt);
        item["expires_epoch"] = N(g.ExpiresAt.ToUnixTimeSeconds());
        item["approved_run_url"] = S(g.ApprovedRunUrl);
        item["actor"] = S(g.Actor);
        SetIf(item, "consumed_by_seq", g.ConsumedBySeq);
        return item;
    }

    private static OverrideGrant ToOverride(Dictionary<string, AttributeValue> i) => new(
        i["environment"].S,
        i["image_digest"].S,
        i["reason"].S,
        Time(i, "expires_at")!.Value,
        i["approved_run_url"].S,
        i["actor"].S,
        Long(i, "consumed_by_seq"));

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue S(DateTimeOffset value) => new() { S = value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) };

    private static AttributeValue N(long value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };

    private static void SetIf(Dictionary<string, AttributeValue> item, string name, string? value)
    {
        if (value is not null)
        {
            item[name] = S(value);
        }
    }

    private static void SetIf(Dictionary<string, AttributeValue> item, string name, long? value)
    {
        if (value is not null)
        {
            item[name] = N(value.Value);
        }
    }

    private static void SetIf(Dictionary<string, AttributeValue> item, string name, DateTimeOffset? value)
    {
        if (value is not null)
        {
            item[name] = S(value.Value);
        }
    }

    private static string? Str(Dictionary<string, AttributeValue> i, string name) =>
        i.TryGetValue(name, out var v) && v.S is not null ? v.S : null;

    private static long? Long(Dictionary<string, AttributeValue> i, string name) =>
        i.TryGetValue(name, out var v) && v.N is not null ? long.Parse(v.N, CultureInfo.InvariantCulture) : null;

    private static DateTimeOffset? Time(Dictionary<string, AttributeValue> i, string name) =>
        Str(i, name) is { } s ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null;
}
