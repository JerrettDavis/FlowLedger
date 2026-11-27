using System.Net;
using System.Net.Mail;
using FlowLedger.Application.Features.Alerts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowLedger.Infrastructure.Outbound;

/// <summary>
/// Typed view of <c>Alerts:Email:*</c>: the optional SMTP channel for alerts (#70). Off by default. The password belongs in a secret
/// (an environment variable, <c>dotnet user-secrets</c> or a mounted config file), never in a committed appsettings file.
/// </summary>
internal sealed class AlertEmailOptions
{
    public const string SectionName = "Alerts:Email";

    public bool Enabled { get; set; }

    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public string? Username { get; set; }

    public string? Password { get; set; }

    public string? From { get; set; }

    /// <summary>One or more recipient addresses separated by commas or semicolons.</summary>
    public string? To { get; set; }

    /// <summary>Upgrade the connection with STARTTLS (the usual port-587 setup). Turn off only for a trusted local relay.</summary>
    public bool StartTls { get; set; } = true;

    public IReadOnlyList<string> Recipients() =>
        (To ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>When the channel is enabled the host must refuse to boot on a config that could never send, instead of failing on the first alert.</summary>
internal sealed class AlertEmailOptionsValidator : IValidateOptions<AlertEmailOptions>
{
    public ValidateOptionsResult Validate(string? name, AlertEmailOptions o)
    {
        if (!o.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(o.Host))
        {
            errors.Add("Alerts:Email:Host is required when Alerts:Email:Enabled is true.");
        }

        if (o.Port is < 1 or > 65535)
        {
            errors.Add($"Alerts:Email:Port must be between 1 and 65535 but is {o.Port}.");
        }

        if (!IsAddress(o.From))
        {
            errors.Add("Alerts:Email:From must be a valid email address.");
        }

        var recipients = o.Recipients();
        if (recipients.Count == 0)
        {
            errors.Add("Alerts:Email:To needs at least one recipient address.");
        }
        else if (recipients.Any(r => !IsAddress(r)))
        {
            errors.Add("Alerts:Email:To contains an address that is not valid (separate several with commas).");
        }

        if (string.IsNullOrEmpty(o.Username) != string.IsNullOrEmpty(o.Password))
        {
            errors.Add("Alerts:Email:Username and Alerts:Email:Password must be set together (or both left empty for an unauthenticated relay).");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static bool IsAddress(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.IndexOfAny(['\r', '\n']) < 0 && MailAddress.TryCreate(value, out _);
}

/// <summary>
/// Sends alert emails over SMTP. Failures are reduced to a short safe summary: no credentials, addresses or server replies are returned
/// or logged.
/// </summary>
internal sealed partial class SmtpAlertEmailSender(IOptions<AlertEmailOptions> options, ILogger<SmtpAlertEmailSender> logger) : IAlertEmailSender
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly AlertEmailOptions _options = options.Value;

    public bool IsEnabled => _options.Enabled;

    public async Task<string?> SendAsync(AlertEmail email, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        if (!_options.Enabled)
        {
            return "Email alerts are not enabled.";
        }

        try
        {
            using var message = new MailMessage { From = new MailAddress(_options.From!), Subject = email.Subject, Body = email.Body };
            foreach (var to in _options.Recipients())
            {
                message.To.Add(to);
            }

            message.Headers.Add("X-FlowLedger-Delivery", email.DeliveryId.ToString("N"));

            using var client = new SmtpClient(_options.Host!, _options.Port)
            {
                EnableSsl = _options.StartTls,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = (int)Timeout.TotalMilliseconds,
            };
            if (!string.IsNullOrEmpty(_options.Username))
            {
                client.Credentials = new NetworkCredential(_options.Username, _options.Password);
            }

            await client.SendMailAsync(message, ct);
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LogFailed(logger, "timeout");
            return "The mail server did not answer in time.";
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or FormatException or System.Net.Sockets.SocketException or IOException)
        {
            LogFailed(logger, ex.GetType().Name);
            return "Could not send the alert email.";
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alert email was not sent ({Reason})")]
    private static partial void LogFailed(ILogger logger, string reason);
}
