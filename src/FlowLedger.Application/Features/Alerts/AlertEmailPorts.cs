namespace FlowLedger.Application.Features.Alerts;

/// <summary>One alert as an email. The text is already redacted per the household's sharing choices (names and amounts).</summary>
/// <param name="Subject">Single line, no CR or LF.</param>
/// <param name="Body">Plain text.</param>
/// <param name="DeliveryId">Stable per alert, so a receiver or mail filter can recognise a replay.</param>
public sealed record AlertEmail(string Subject, string Body, Guid DeliveryId);

/// <summary>
/// Optional email channel for alerts (#70). Off unless the host configured <c>Alerts:Email</c>; when <see cref="IsEnabled"/> is false
/// the delivery service never calls <see cref="SendAsync"/>.
/// </summary>
public interface IAlertEmailSender
{
    bool IsEnabled { get; }

    /// <returns>Null on success, otherwise a short, safe error summary (never credentials, addresses or server replies).</returns>
    Task<string?> SendAsync(AlertEmail email, CancellationToken ct = default);
}
