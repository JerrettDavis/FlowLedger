using System.Net;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Security;

[Collection("ApiIntegration")]
public sealed class ReadinessEndpointTests(ReadinessApiFactory factory)
{
    [Fact]
    public async Task Stopping_postgres_turns_ready_503_while_alive_stays_200()
    {
        var client = factory.CreateClient();

        (await client.GetAsync("/ready")).StatusCode.Should().Be(HttpStatusCode.OK, "database is up and migrated");
        (await client.GetAsync("/alive")).StatusCode.Should().Be(HttpStatusCode.OK);

        await factory.StopDatabaseAsync();

        var ready = await client.GetAsync("/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ready.Content.ReadAsStringAsync()).Should().Be("Unhealthy");
        (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await client.GetAsync("/alive")).StatusCode.Should().Be(HttpStatusCode.OK, "liveness must not depend on Postgres");
    }
}
