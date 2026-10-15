using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Release.Core.Deployments;
using Testcontainers.DynamoDb;

[assembly: AssemblyFixture(typeof(Release.IntegrationTests.DynamoFixture))]

namespace Release.IntegrationTests;

/// <summary>One DynamoDB Local container per run; each test class gets its own table.</summary>
public sealed class DynamoFixture : IAsyncLifetime
{
    public const string Image =
        "amazon/dynamodb-local:3.3.1@sha256:ff89bd48ff32cd8d9be5fee8873b65b8854dc408f1afe881be6eb00247bc0dab";

    private readonly DynamoDbContainer container = new DynamoDbBuilder(Image).Build();

    public IAmazonDynamoDB Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await container.StartAsync().ConfigureAwait(false);
        Client = new AmazonDynamoDBClient(
            new BasicAWSCredentials("local", "local"),
            new AmazonDynamoDBConfig { ServiceURL = container.GetConnectionString(), AuthenticationRegion = "us-east-1" });
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        await container.DisposeAsync().ConfigureAwait(false);
    }

    public async Task<DynamoDeploymentStore> NewStoreAsync()
    {
        var table = $"deployments-{Guid.NewGuid():N}";
        await DynamoDeploymentStore.CreateTableAsync(Client, table, TestContext.Current.CancellationToken).ConfigureAwait(false);
        return new DynamoDeploymentStore(Client, table);
    }
}
