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
    public void Verify(ReadOnlySpan<byte> rawBody, string? signatureHeader)
    {
        var expected = ComputeSignature(rawBody);
        var presented = Encoding.UTF8.GetBytes(signatureHeader ?? string.Empty);

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), presented))
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
