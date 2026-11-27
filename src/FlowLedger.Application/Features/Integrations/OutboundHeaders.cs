using System.Text;
using System.Text.RegularExpressions;

namespace FlowLedger.Application.Features.Integrations;

/// <summary>Validation and assembly of the headers on outbound integration requests.</summary>
internal static partial class OutboundHeaders
{
    /// <summary>Headers the caller may not set: they would let a definition spoof routing, framing or the authentication we add.</summary>
    internal static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-length", "content-type", "transfer-encoding", "connection", "upgrade", "te", "trailer", "expect",
        "authorization", "proxy-authorization", "cookie", "set-cookie", "soapaction",
        IntegrationService.SignatureHeader, IntegrationService.TimestampHeader, IntegrationService.EventHeader, IntegrationService.DeliveryHeader,
    };

    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,64}$")]
    internal static partial Regex HeaderName();

    internal static List<KeyValuePair<string, string>> Build(
        IntegrationConfig config, IntegrationSecrets secrets, IReadOnlyDictionary<string, string> extra)
    {
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var (k, v) in config.Headers)
        {
            headers.Add(new(k, v));
        }

        foreach (var (k, v) in extra)
        {
            headers.RemoveAll(h => h.Key.Equals(k, StringComparison.OrdinalIgnoreCase));
            headers.Add(new(k, v));
        }

        if (secrets.Credential is { } credential)
        {
            switch (config.Auth)
            {
                case IntegrationAuth.Bearer:
                    headers.Add(new("Authorization", "Bearer " + credential));
                    break;
                case IntegrationAuth.Basic:
                    headers.Add(new("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes((config.AuthUser ?? "") + ":" + credential))));
                    break;
                case IntegrationAuth.ApiKey:
                    headers.Add(new(config.AuthUser ?? "X-API-Key", credential));
                    break;
            }
        }

        if (headers.All(h => !h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)))
        {
            headers.Add(new("User-Agent", "FlowLedger-Integrations/1"));
        }

        return headers;
    }

    internal static bool TryClean(Dictionary<string, string>? raw, out Dictionary<string, string> clean, out string? error)
    {
        clean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = null;
        if (raw is null)
        {
            return true;
        }

        if (raw.Count > IntegrationService.MaxHeaders)
        {
            error = $"Use at most {IntegrationService.MaxHeaders} extra headers.";
            return false;
        }

        foreach (var (key, value) in raw)
        {
            var k = key?.Trim() ?? "";
            var v = value?.Trim() ?? "";
            if (!HeaderName().IsMatch(k) || Reserved.Contains(k))
            {
                error = $"'{k}' is not an allowed header name (authentication, framing and signature headers are managed for you).";
                return false;
            }

            if (v.Length > 1024 || v.Any(char.IsControl))
            {
                error = $"The value of '{k}' is invalid.";
                return false;
            }

            clean[k] = v;
        }

        return true;
    }
}
