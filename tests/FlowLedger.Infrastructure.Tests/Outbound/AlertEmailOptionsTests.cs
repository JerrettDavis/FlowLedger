using FlowLedger.Infrastructure.Outbound;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace FlowLedger.Infrastructure.Tests.Outbound;

/// <summary>#70: the SMTP alert channel is off by default and refuses to boot half-configured.</summary>
public sealed class AlertEmailOptionsTests
{
    private static AlertEmailOptions Valid() => new()
    {
        Enabled = true, Host = "smtp.example.com", Port = 587, Username = "u", Password = "p", From = "alerts@example.com", To = "me@example.com",
    };

    private static string[] Failures(AlertEmailOptions o) =>
        new AlertEmailOptionsValidator().Validate(null, o).Failures?.ToArray() ?? [];

    [Fact]
    public void The_channel_is_off_by_default_and_needs_no_other_setting()
    {
        var options = new AlertEmailOptions();

        options.Enabled.Should().BeFalse();
        Failures(options).Should().BeEmpty();
    }

    [Fact]
    public void A_complete_enabled_configuration_is_valid()
    {
        Failures(Valid()).Should().BeEmpty();
    }

    [Fact]
    public void An_enabled_channel_without_a_host_a_sender_or_recipients_fails_validation()
    {
        var o = Valid();
        o.Host = " ";
        o.From = null;
        o.To = null;

        Failures(o).Should().HaveCount(3).And.Contain(f => f.Contains("Host")).And.Contain(f => f.Contains("From")).And.Contain(f => f.Contains("To"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void A_port_outside_the_range_fails_validation(int port)
    {
        var o = Valid();
        o.Port = port;

        Failures(o).Should().ContainSingle().Which.Should().Contain("Port");
    }

    [Fact]
    public void A_bad_recipient_or_a_username_without_a_password_fails_validation()
    {
        var bad = Valid();
        bad.To = "me@example.com; not-an-address";
        var half = Valid();
        half.Password = null;

        Failures(bad).Should().ContainSingle().Which.Should().Contain("Alerts:Email:To");
        Failures(half).Should().ContainSingle().Which.Should().Contain("Password");
    }

    [Fact]
    public void Header_injection_in_an_address_is_rejected()
    {
        var o = Valid();
        o.From = "a@example.com\r\nBcc: x@example.com";

        Failures(o).Should().ContainSingle().Which.Should().Contain("From");
    }

    [Fact]
    public void Several_recipients_are_split_on_commas_and_semicolons()
    {
        var o = Valid();
        o.To = "a@example.com, b@example.com;c@example.com";

        o.Recipients().Should().Equal("a@example.com", "b@example.com", "c@example.com");
    }

    [Fact]
    public void The_section_binds_from_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Alerts:Email:Enabled"] = "true",
            ["Alerts:Email:Host"] = "mail.local",
            ["Alerts:Email:StartTls"] = "false",
            ["Alerts:Email:Port"] = "25",
        }).Build();

        var o = config.GetSection(AlertEmailOptions.SectionName).Get<AlertEmailOptions>()!;

        o.Enabled.Should().BeTrue();
        o.Host.Should().Be("mail.local");
        o.StartTls.Should().BeFalse();
        o.Port.Should().Be(25);
    }
}
