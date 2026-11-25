using System.Security.Cryptography;
using System.Text;
using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Integrations.Quiltt;

/// <summary>
/// Verifies the <c>Quiltt-Signature</c> header: <c>t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256&gt;</c> over <c>"{t}.{rawBody}"</c>,
/// keyed by the webhook secret, constant-time compared, with a timestamp tolerance against replay.
/// The exact header format is isolated here so it can be adjusted against the Quiltt sandbox in one place.
/// </summary>
internal sealed class QuilttWebhookVerifier
{
    private readonly byte[] _secret;
    private readonly TimeSpan _tolerance;
    private readonly TimeProvider _time;

    public QuilttWebhookVerifier(string secret, TimeSpan tolerance, TimeProvider? time = null)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("Quiltt webhook secret must not be empty.", nameof(secret));
        }

        _secret = Encoding.UTF8.GetBytes(secret);
        _tolerance = tolerance;
        _time = time ?? TimeProvider.System;
    }

    public void Verify(ReadOnlySpan<byte> rawBody, string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            throw new InvalidWebhookSignatureException("Quiltt webhook signature header is missing.");
        }

        string? t = null;
        var v1 = new List<string>();
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) { continue; }
            switch (kv[0].Trim())
            {
                case "t": t = kv[1].Trim(); break;
                case "v1": v1.Add(kv[1].Trim()); break;
            }
        }

        if (t is null || v1.Count == 0 || !long.TryParse(t, out var ts))
        {
            throw new InvalidWebhookSignatureException("Quiltt webhook signature header is malformed.");
        }

        var age = _time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(ts);
        if (age.Duration() > _tolerance)
        {
            throw new InvalidWebhookSignatureException("Quiltt webhook timestamp is outside the allowed window.");
        }

        var expected = Encoding.UTF8.GetBytes(Sign(t, rawBody));
        foreach (var candidate in v1)
        {
            var presented = Encoding.UTF8.GetBytes(candidate.ToLowerInvariant());
            if (presented.Length == expected.Length && CryptographicOperations.FixedTimeEquals(presented, expected))
            {
                return;
            }
        }

        throw new InvalidWebhookSignatureException("Quiltt webhook signature verification failed.");
    }

    public string Sign(string timestamp, ReadOnlySpan<byte> rawBody)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var payload = new byte[prefix.Length + rawBody.Length];
        prefix.CopyTo(payload, 0);
        rawBody.CopyTo(payload.AsSpan(prefix.Length));
        return Convert.ToHexString(HMACSHA256.HashData(_secret, payload)).ToLowerInvariant();
    }
}
