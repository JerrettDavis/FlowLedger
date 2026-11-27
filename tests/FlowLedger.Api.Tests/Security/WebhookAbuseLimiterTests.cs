using FlowLedger.Api.Endpoints;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Security;

/// <summary>#152: failed and verified webhook outcomes are budgeted separately, per provider and source.</summary>
public sealed class WebhookAbuseLimiterTests
{
    [Fact]
    public void Failed_verifications_are_capped_per_provider_and_ip_without_touching_other_sources()
    {
        using var limiter = new WebhookAbuseLimiter(invalidPerIpPerMinute: 3, verifiedPerProviderPerMinute: 100);

        Enumerable.Range(0, 5).Count(_ => limiter.AllowInvalidResponse("mx", "203.0.113.5")).Should().Be(3);
        limiter.AllowInvalidResponse("mx", "203.0.113.6").Should().BeTrue("another source has its own budget");
        limiter.AllowInvalidResponse("quiltt", "203.0.113.5").Should().BeTrue("another provider has its own budget");
    }

    [Fact]
    public void Verified_deliveries_are_never_spent_by_invalid_traffic()
    {
        using var limiter = new WebhookAbuseLimiter(invalidPerIpPerMinute: 1, verifiedPerProviderPerMinute: 4);
        for (var i = 0; i < 50; i++)
        {
            limiter.AllowInvalidResponse("mx", "198.51.100.9");
        }

        Enumerable.Range(0, 6).Count(_ => limiter.AllowVerifiedDelivery("mx")).Should().Be(4, "only the verified budget limits verified deliveries");
    }
}
