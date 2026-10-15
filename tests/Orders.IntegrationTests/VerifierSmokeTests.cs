using Release.Core.Verification;

namespace Orders.IntegrationTests;

/// <summary>The release verifier's smoke test must pass against a correct build of the API.</summary>
public sealed class VerifierSmokeTests(DatabaseFixture database) : OrdersApiTestBase(database)
{
    [Fact]
    public async Task Smoke_test_passes_every_step_against_the_api()
    {
        var steps = await new HttpSmokeTest(Client).RunAsync(1, Ct);

        Assert.Equal(
            ["ready", "create", "idempotent-replay", "read", "transition", "illegal-transition-refused"],
            steps.Select(s => s.Name));
        Assert.All(steps, s => Assert.True(s.Passed, $"{s.Name}: {s.Detail}"));
    }
}
