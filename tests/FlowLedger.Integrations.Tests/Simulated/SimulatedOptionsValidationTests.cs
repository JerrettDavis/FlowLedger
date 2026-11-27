using FlowLedger.Integrations.Mx;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Tests.Simulated;

/// <summary>
/// Validates the fast-fail behaviour of <see cref="MxCredentialsOptionsValidator"/>:
/// disabled = no credential requirements; enabled = all required fields must be present.
/// </summary>
public sealed class SimulatedOptionsValidationTests
{
    private static ValidateOptionsResult Validate(MxCredentialsOptions opts)
    {
        var validator = new MxCredentialsOptionsValidator();
        return validator.Validate(null, opts);
    }

    [Fact]
    public void Disabled_with_no_credentials_is_valid()
    {
        var opts = new MxCredentialsOptions { Enabled = false };
        Validate(opts).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Disabled_with_partial_credentials_is_still_valid()
    {
        var opts = new MxCredentialsOptions
        {
            Enabled = false,
            ApiKey = "some-key",
            // ClientId and others missing — still valid when disabled
        };
        Validate(opts).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Enabled_with_all_credentials_is_valid()
    {
        var opts = new MxCredentialsOptions
        {
            Enabled = true,
            ApiKey = "api-key-value",
            ClientId = "client-id-value",
            BaseUrl = "https://int-api.mx.com",
            WebhookSecret = "webhook-secret-value",
        };
        Validate(opts).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Enabled_missing_api_key_fails()
    {
        var opts = new MxCredentialsOptions
        {
            Enabled = true,
            ClientId = "client-id",
            BaseUrl = "https://api.mx.com",
            WebhookSecret = "secret",
        };
        var result = Validate(opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains(nameof(MxCredentialsOptions.ApiKey)));
    }

    [Fact]
    public void Enabled_missing_client_id_fails()
    {
        var opts = new MxCredentialsOptions
        {
            Enabled = true,
            ApiKey = "api-key",
            BaseUrl = "https://api.mx.com",
            WebhookSecret = "secret",
        };
        var result = Validate(opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains(nameof(MxCredentialsOptions.ClientId)));
    }

    [Fact]
    public void Enabled_missing_base_url_fails()
    {
        var opts = new MxCredentialsOptions
        {
            Enabled = true,
            ApiKey = "api-key",
            ClientId = "client-id",
            WebhookSecret = "secret",
        };
        var result = Validate(opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains(nameof(MxCredentialsOptions.BaseUrl)));
    }

    [Fact]
    public void Enabled_missing_webhook_secret_fails()
    {
        var opts = new MxCredentialsOptions
        {
            Enabled = true,
            ApiKey = "api-key",
            ClientId = "client-id",
            BaseUrl = "https://api.mx.com",
        };
        var result = Validate(opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains(nameof(MxCredentialsOptions.WebhookSecret)));
    }

    [Fact]
    public void Enabled_missing_all_fields_reports_all_failures()
    {
        var opts = new MxCredentialsOptions { Enabled = true };
        var result = Validate(opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().HaveCount(4, because: "all 4 required fields are missing");
    }

    [Fact]
    public void Default_options_are_disabled_and_valid()
    {
        var opts = new MxCredentialsOptions();
        opts.Enabled.Should().BeFalse("default must be disabled so no API key is needed");
        Validate(opts).Succeeded.Should().BeTrue();
    }
}
