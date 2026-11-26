using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

[Collection("ApiIntegration")]
public sealed class StatusBuildInfoTests(FlowLedgerApiFactory factory)
{
    [Fact]
    public async Task Status_reports_version_and_commit()
    {
        using var client = factory.CreateAuthenticatedClient();
        var body = await client.GetFromJsonAsync<JsonElement>("/api/status");
        body.GetProperty("status").GetString().Should().Be("ok");
        body.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("commit").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("v1.2.3", "abc123", "9.9.9+zzz", "v1.2.3", "abc123")]
    [InlineData(null, null, "1.0.0+deadbeef", "1.0.0", "deadbeef")]
    [InlineData("", " ", "1.0.0", "1.0.0", "unknown")]
    [InlineData(null, null, null, "unknown", "unknown")]
    public void Resolve_prefers_the_environment_then_the_assembly_then_unknown(string? v, string? c, string? informational, string version, string commit)
        => BuildInfo.Resolve(v, c, informational).Should().Be(new BuildInfo(version, commit));
}
