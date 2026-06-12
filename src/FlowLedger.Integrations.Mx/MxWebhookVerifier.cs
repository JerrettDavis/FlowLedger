using System.Security.Cryptography;
using System.Text;
using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Integrations.Mx;

/// <summary>
/// Verifies inbound MX webhook authenticity with an HMAC-SHA256 signature over the raw
/// request body, keyed by <c>Mx:WebhookSecret</c>, using a constant-time comparison
/// (<see cref="CryptographicOperations.FixedTimeEquals"/>) to avoid timing side-channels.
/// Mirrors the proven pattern in the Simulated provider.
///
/// Design note: MX Platform's own webhook transport authenticates via Basic auth / mTLS /
/// OAuth2 rather than an HMAC signature header. FlowLedger nonetheless enforces its own
/// shared-secret HMAC at the webhook ingress as an additional, transport-independent
/// integrity check (and to satisfy the IFinancialDataProvider webhook-verification contract).
/// The signature is expected as a lowercase hex string in the provider's signature header.
/// </summary>
internal sealed class MxWebhookVerifier
{
    private readonly byte[] _secretBytes;

    public MxWebhookVerifier(string webhookSecret)
    {
        if (string.IsNullOrWhiteSpace(webhookSecret))
        {
            throw new ArgumentException("MX webhook secret must not be empty.", nameof(webhookSecret));
        }

        _secretBytes = Encoding.UTF8.GetBytes(webhookSecret);
    }

    /// <summary>
    /// Throws <see cref="InvalidWebhookSignatureException"/> when the presented signature does
    /// not match the HMAC computed over <paramref name="rawBody"/>.
    /// </summary>
    /// <remarks>
    /// The explicit length check before <see cref="CryptographicOperations.FixedTimeEquals"/>
    /// ensures we reject a missing/empty header immediately (no HMAC is needed) and that the
    /// constant-time comparison always operates on equal-length inputs (a hex-encoded HMAC-SHA256
    /// is always 64 ASCII characters). This preserves true constant-time behaviour on the matched
    /// path while failing fast on obvious mismatches.
    /// </remarks>
    public void Verify(ReadOnlySpan<byte> rawBody, string? signatureHeader)
    {
        // Reject null/empty signature before doing any crypto work.
        if (string.IsNullOrEmpty(signatureHeader))
        {
            throw new InvalidWebhookSignatureException(
                "MX webhook signature header is missing or empty.");
        }

        var expectedBytes = Encoding.UTF8.GetBytes(ComputeSignature(rawBody));
        var presentedBytes = Encoding.UTF8.GetBytes(signatureHeader);

        // Guard equal length so FixedTimeEquals runs on matched-length spans (true constant time
        // on the happy path; length mismatch is itself a non-secret observable and can fast-fail).
        if (expectedBytes.Length != presentedBytes.Length
            || !CryptographicOperations.FixedTimeEquals(expectedBytes, presentedBytes))
        {
            throw new InvalidWebhookSignatureException(
                "MX webhook signature verification failed.");
        }
    }

    /// <summary>Computes the lowercase-hex HMAC-SHA256 signature for a payload.</summary>
    public string ComputeSignature(ReadOnlySpan<byte> rawBody)
    {
        var hash = HMACSHA256.HashData(_secretBytes, rawBody);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
