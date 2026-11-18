using System.Globalization;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace FlowLedger.Web.Auth;

/// <summary>
/// The auth cookie carries the tenant resolved at sign-in and lives for up to 14 days. If that login is later unlinked
/// from the tenant (or moved to another one with a link code), the cookie would otherwise keep granting the old
/// tenant's data. Every <see cref="Interval"/> the mapping is re-checked against the API and the cookie is rejected
/// on a definite mismatch. API outages never sign users out: the check is simply retried on the next request.
/// Note: this runs on HTTP requests (page loads, circuit start), not inside an already-open Blazor circuit.
/// </summary>
public static class TenantMappingValidator
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    internal const string CheckedItem = "fl_mapping_checked";

    public enum Outcome
    {
        /// <summary>Checked recently; nothing to do.</summary>
        NotDue,
        /// <summary>The login still maps to the tenant in the cookie.</summary>
        Valid,
        /// <summary>Definite mismatch (or the cookie lacks tenant/user claims): sign out.</summary>
        Reject,
        /// <summary>The API could not answer; keep the principal and retry next request.</summary>
        Unknown,
    }

    /// <param name="listUserIdsOnTenant">Returns the user ids linked to the tenant, or null when the API could not answer.</param>
    public static async Task<Outcome> EvaluateAsync(
        ClaimsPrincipal? principal, DateTimeOffset? lastChecked, DateTimeOffset now,
        Func<Guid, CancellationToken, Task<IReadOnlyCollection<Guid>?>> listUserIdsOnTenant, CancellationToken ct = default)
    {
        if (lastChecked is { } last && now - last < Interval && now >= last)
        {
            return Outcome.NotDue;
        }

        if (!Guid.TryParse(principal?.FindFirst(AuthSetup.TenantClaim)?.Value, out var tenant)
            || !Guid.TryParse(principal?.FindFirst(AuthSetup.UserClaim)?.Value, out var user))
        {
            return Outcome.Reject;
        }

        IReadOnlyCollection<Guid>? users;
        try
        {
            users = await listUserIdsOnTenant(tenant, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return Outcome.Unknown;
        }

        return users is null ? Outcome.Unknown : users.Contains(user) ? Outcome.Valid : Outcome.Reject;
    }

    /// <summary>Cookie <c>OnValidatePrincipal</c> handler.</summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? last = ctx.Properties.Items.TryGetValue(CheckedItem, out var raw)
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;

        var services = ctx.HttpContext.RequestServices;
        var outcome = await EvaluateAsync(ctx.Principal, last, now,
            (tenant, ct) => ListUserIdsAsync(services, tenant, ct), ctx.HttpContext.RequestAborted);

        switch (outcome)
        {
            case Outcome.Valid:
                ctx.Properties.Items[CheckedItem] = now.ToString("O", CultureInfo.InvariantCulture);
                ctx.ShouldRenew = true;
                break;
            case Outcome.Reject:
                services.GetService<ILoggerFactory>()?.CreateLogger(typeof(TenantMappingValidator))
                    .LogInformation("Signing out a session whose login no longer maps to its tenant.");
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                break;
        }
    }

    private sealed record IdentityRow(Guid UserId);

    private static async Task<IReadOnlyCollection<Guid>?> ListUserIdsAsync(IServiceProvider services, Guid tenant, CancellationToken ct)
    {
        var apiKey = services.GetRequiredService<IOptions<FlowLedger.Web.ApiClient.ApiAuthOptions>>().Value.Key;
        var http = services.GetRequiredService<IHttpClientFactory>().CreateClient(AuthSetup.IdentityClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/users/identities");
        req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        req.Headers.TryAddWithoutValidation("X-Tenant-Id", tenant.ToString());
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null; // 401/5xx etc: not a definite answer about the mapping
        }

        var rows = await resp.Content.ReadFromJsonAsync<List<IdentityRow>>(cancellationToken: ct);
        return rows?.Select(r => r.UserId).ToHashSet();
    }
}
