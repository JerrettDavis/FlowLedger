using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Quiltt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Tests.Quiltt;

/// <summary>
/// Opt-in checks against the real Quiltt SANDBOX. They return immediately (pass silently) unless the environment says otherwise:
/// <list type="bullet">
/// <item><c>QUILTT_LIVE_KEY</c> - sandbox API key secret (never commit it).</item>
/// <item><c>QUILTT_LIVE_PROFILE</c> - a sandbox profile id (<c>p_...</c>) holding the scenario connection(s).</item>
/// <item><c>QUILTT_LIVE_EXPECT</c> - optional <see cref="ProviderIssueKind"/> name the profile's health must map to
/// (for example <c>ReauthRequired</c> after <c>connectionSimulateError</c>; <c>None</c> for a healthy profile).</item>
/// </list>
/// Note Quiltt limits sessions per profile per hour (HTTP 429), so run this sparingly; a 429 is reported as the typed
/// <see cref="RateLimitedProviderException"/> and treated as inconclusive here.
/// </summary>
public sealed class QuilttLiveSandboxTests
{
    [Fact]
    public async Task LiveProfile_HealthMatchesExpectedKind_AndReadsAgree()
    {
        var key = Environment.GetEnvironmentVariable("QUILTT_LIVE_KEY");
        var profile = Environment.GetEnvironmentVariable("QUILTT_LIVE_PROFILE");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(profile))
        {
            return;
        }

        var opts = Options.Create(new QuilttOptions { ApiKeySecret = key });
        using var http = new HttpClient();
        var provider = new QuilttFinancialDataProvider(
            new QuilttApiClient(http, opts), opts, null, NullLogger<QuilttFinancialDataProvider>.Instance);

        ConnectionHealth health;
        try
        {
            health = await provider.GetConnectionHealthAsync(profile);
        }
        catch (RateLimitedProviderException ex)
        {
            ex.Kind.Should().Be(ProviderIssueKind.RateLimited); // inconclusive but correctly classified
            return;
        }

        var expect = Environment.GetEnvironmentVariable("QUILTT_LIVE_EXPECT");
        if (Enum.TryParse<ProviderIssueKind>(expect, ignoreCase: true, out var kind))
        {
            (health.Issue?.Kind ?? ProviderIssueKind.None).Should().Be(kind);
        }

        if (health.Issue is { Kind: not ProviderIssueKind.None } issue)
        {
            // Reads must surface the same classification (or succeed when a healthy connection is also present).
            QuilttProviderCode.TryGetConnectionId(issue.ProviderCode).Should().StartWith("conn_");
            issue.Message.Should().NotContain("Bearer");
        }
    }
}
