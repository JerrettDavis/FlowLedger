using System.Security.Cryptography;
using System.Text;

namespace FlowLedger.Api.Auth;

/// <summary>Which configured key (if any) a supplied credential matched.</summary>
public enum ApiKeyMatch
{
    None,
    Current,
    Previous,
}

/// <summary>
/// Strength rules and comparison for <c>Api:Key</c> / <c>Api:PreviousKey</c>. Strength is enforced by the non-Development startup
/// guard (Development keeps its public dev key); comparison is constant-time and independent of key length.
/// </summary>
public static class ApiKeyPolicy
{
    public const int MinimumLength = 32;

    // Well-known placeholder prefixes (case-insensitive). Anything starting with one of these is rejected outside Development,
    // even when padded past the minimum length.
    private static readonly string[] DeniedPrefixes =
    [
        "changeme",
        "change-me",
        "password",
        "secret",
        "dev-local-key",
        "ci-test-api-key",
    ];

    /// <summary>Returns an error message when <paramref name="key"/> is too weak, otherwise null.</summary>
    public static string? ValidateKey(string? key, string settingName)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return $"{settingName} must be set to a non-empty value.";
        }

        if (key.Length < MinimumLength)
        {
            return $"{settingName} must be at least {MinimumLength} characters (e.g. `openssl rand -hex 32`).";
        }

        foreach (var denied in DeniedPrefixes)
        {
            if (key.StartsWith(denied, StringComparison.OrdinalIgnoreCase))
            {
                return $"{settingName} looks like a well-known placeholder ('{denied}...'). " +
                       "Generate a strong random key (e.g. `openssl rand -hex 32`).";
            }
        }

        return null;
    }

    /// <summary>Validates the current key and, when set, the rotation key (strong and different from the current key).</summary>
    public static string? ValidateOptions(ApiOptions options)
    {
        var error = ValidateKey(options.Key, "Api:Key");
        if (error is not null)
        {
            return error;
        }

        if (string.IsNullOrEmpty(options.PreviousKey))
        {
            return null;
        }

        error = ValidateKey(options.PreviousKey, "Api:PreviousKey");
        if (error is not null)
        {
            return error;
        }

        return string.Equals(options.PreviousKey, options.Key, StringComparison.Ordinal)
            ? "Api:PreviousKey must differ from Api:Key (remove it once rotation is finished)."
            : null;
    }

    /// <summary>
    /// Constant-time match against the configured keys. Both sides are hashed first so the comparison does not leak the
    /// configured key length. The previous key only matches while it is configured.
    /// </summary>
    public static ApiKeyMatch Match(string supplied, ApiOptions options)
    {
        if (string.IsNullOrEmpty(supplied))
        {
            return ApiKeyMatch.None;
        }

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var current = !string.IsNullOrEmpty(options.Key)
            && CryptographicOperations.FixedTimeEquals(suppliedHash, SHA256.HashData(Encoding.UTF8.GetBytes(options.Key)));
        var previous = !string.IsNullOrEmpty(options.PreviousKey)
            && CryptographicOperations.FixedTimeEquals(suppliedHash, SHA256.HashData(Encoding.UTF8.GetBytes(options.PreviousKey)));

        // Evaluate both before branching so timing does not reveal which key matched.
        return current ? ApiKeyMatch.Current : previous ? ApiKeyMatch.Previous : ApiKeyMatch.None;
    }
}
