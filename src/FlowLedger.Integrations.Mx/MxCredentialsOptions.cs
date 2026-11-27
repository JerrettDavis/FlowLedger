using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Mx;

/// <summary>
/// Configuration section "Mx" — controls whether the real MX provider is active
/// and supplies its credentials.
///
/// Default state: <see cref="Enabled"/> = false.
/// In this state no API key is required and the Simulated provider is used.
/// Setting <see cref="Enabled"/> = true without supplying all required fields
/// causes a fast-fail at startup via <see cref="MxCredentialsOptionsValidator"/>.
/// </summary>
public sealed class MxCredentialsOptions
{
    /// <summary>Configuration section key used by <c>IConfiguration.GetSection</c>.</summary>
    public const string SectionName = "Mx";

    /// <summary>
    /// When false (the default), the Simulated provider is used and no API credentials
    /// are required.  Set true only when a real MX account is available.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>MX API key.  Required only when <see cref="Enabled"/> is true.</summary>
    public string? ApiKey { get; set; }

    /// <summary>MX client ID.  Required only when <see cref="Enabled"/> is true.</summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Base URL for the MX API (e.g. "https://int-api.mx.com" for sandbox).
    /// Required only when <see cref="Enabled"/> is true.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>HMAC secret used to verify inbound MX webhooks.  Required when Enabled.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Deployment environment hint ("sandbox" | "production").</summary>
    public string Environment { get; set; } = "sandbox";
}

/// <summary>
/// Validates <see cref="MxCredentialsOptions"/> at startup.
/// Fails fast ONLY when <c>Enabled = true</c> and a required field is absent.
/// When disabled, all credential fields are optional.
/// </summary>
public sealed class MxCredentialsOptionsValidator : IValidateOptions<MxCredentialsOptions>
{
    public ValidateOptionsResult Validate(string? name, MxCredentialsOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            failures.Add($"{MxCredentialsOptions.SectionName}:{nameof(MxCredentialsOptions.ApiKey)} is required when Mx:Enabled is true.");
        }

        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            failures.Add($"{MxCredentialsOptions.SectionName}:{nameof(MxCredentialsOptions.ClientId)} is required when Mx:Enabled is true.");
        }

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            failures.Add($"{MxCredentialsOptions.SectionName}:{nameof(MxCredentialsOptions.BaseUrl)} is required when Mx:Enabled is true.");
        }

        if (string.IsNullOrWhiteSpace(options.WebhookSecret))
        {
            failures.Add($"{MxCredentialsOptions.SectionName}:{nameof(MxCredentialsOptions.WebhookSecret)} is required when Mx:Enabled is true.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
