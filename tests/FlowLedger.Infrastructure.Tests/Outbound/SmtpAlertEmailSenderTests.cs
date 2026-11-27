using FlowLedger.Infrastructure.Outbound;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowLedger.Infrastructure.Tests.Outbound;

/// <summary>#70 / INV-01: the one host-wide mail recipient only ever receives the household tenant's alerts.</summary>
public sealed class SmtpAlertEmailSenderTests
{
    private static readonly Guid Household = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    private static SmtpAlertEmailSender Sender(bool enabled) => new(
        Options.Create(new AlertEmailOptions { Enabled = enabled, Host = "smtp.example.com", From = "a@example.com", To = "b@example.com" }),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Tenancy:LegacyTenantId"] = Household.ToString() }).Build(),
        NullLogger<SmtpAlertEmailSender>.Instance);

    [Fact]
    public void The_household_tenant_may_use_an_enabled_channel()
    {
        Sender(enabled: true).IsEnabledFor(Household).Should().BeTrue();
    }

    [Fact]
    public void Any_other_tenant_never_may()
    {
        Sender(enabled: true).IsEnabledFor(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void A_disabled_channel_serves_nobody()
    {
        Sender(enabled: false).IsEnabledFor(Household).Should().BeFalse();
    }

    [Fact]
    public async Task Sending_on_a_disabled_channel_reports_it_and_does_not_connect()
    {
        var error = await Sender(enabled: false).SendAsync(new FlowLedger.Application.Features.Alerts.AlertEmail("s", "b", Guid.NewGuid()));

        error.Should().Be("Email alerts are not enabled.");
    }
}
