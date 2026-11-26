using System.Net;
using System.Security.Cryptography;
using System.Text;
using FlowLedger.ServiceDefaults;

namespace FlowLedger.Api.Endpoints;

/// <summary>
/// Access gate for the whole-database export / import / backup-now endpoints, applied IN ADDITION to the household-tenant check.
/// <list type="bullet">
/// <item><c>DataAdmin:Key</c> set (>= 32 chars): the caller must also send it as <c>X-Admin-Token</c>. The loopback address check is
/// then only enforced when <c>DataAdmin:RequireLoopback=true</c>.</item>
/// <item>No key (legacy behaviour): the caller must be a DIRECT loopback peer. Forwarded headers cannot satisfy this: the socket
/// peer as it was before forwarded-headers rewriting is used, and a request that carries any forwarding header (i.e. arrived via a
/// co-located reverse proxy) is refused. <c>DataAdmin:AllowRemote=true</c> still disables the check (explicit opt-out).</item>
/// </list>
/// </summary>
internal static class DataAdminGate
{
    public const string TokenHeader = "X-Admin-Token";
    public const int MinimumKeyLength = 32;

    internal readonly record struct Result(bool Allowed, string? Title = null, string? Detail = null);

    internal static Result Evaluate(IConfiguration config, HttpContext http)
    {
        var key = config["DataAdmin:Key"];
        if (!string.IsNullOrEmpty(key))
        {
            if (key.Length < MinimumKeyLength)
            {
                return Deny("Data administration is misconfigured", $"DataAdmin:Key must be at least {MinimumKeyLength} characters.");
            }

            var supplied = http.Request.Headers[TokenHeader].ToString();
            if (!TokenMatches(key, supplied))
            {
                return Deny("Data administration requires the admin token", $"Send the DataAdmin:Key value in the {TokenHeader} header.");
            }

            return config.GetValue("DataAdmin:RequireLoopback", false) && !IsDirectLoopback(http)
                ? Deny("Data administration is only available from the host itself", "DataAdmin:RequireLoopback=true.")
                : new Result(true);
        }

        if (config.GetValue("DataAdmin:AllowRemote", false))
        {
            return new Result(true); // explicit operator opt-out
        }

        // #139: outside Development an address is not a credential. A configured DataAdmin:Key is required; the loopback shortcut
        // remains for local development only (and for contexts that carry no host environment, such as unit tests).
        var environment = http.RequestServices?.GetService<IHostEnvironment>();
        if (environment is not null && !environment.IsDevelopment())
        {
            return Deny(
                "Data administration requires an admin key",
                "Set DataAdmin:Key (32+ characters) on the API and the web app; callers send it as X-Admin-Token.");
        }

        if (IsDirectLoopback(http))
        {
            return new Result(true);
        }

        return Deny(
            "Data administration is only available from the host itself",
            "Set DataAdmin:Key (and send X-Admin-Token) to allow it from other addresses.");
    }

    /// <summary>True for a caller connecting straight from loopback (no proxy hop): the original peer, and the current address,
    /// are loopback and no forwarding header is present. An unknown peer (in-process test host) counts as local.</summary>
    internal static bool IsDirectLoopback(HttpContext http)
    {
        if (ForwardedHeadersSetup.WasForwarded(http))
        {
            return false;
        }

        var peer = ForwardedHeadersSetup.PeerAddress(http);
        var current = http.Connection.RemoteIpAddress;
        return (peer is null || IPAddress.IsLoopback(peer)) && (current is null || IPAddress.IsLoopback(current));
    }

    private static bool TokenMatches(string key, string supplied) =>
        supplied.Length > 0
        && FlowLedger.SharedKernel.ConstantTime.SecretEquals(supplied, key);

    private static Result Deny(string title, string detail) => new(false, title, detail);
}
