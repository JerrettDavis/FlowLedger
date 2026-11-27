using Microsoft.Playwright;

namespace FlowLedger.E2E.Tests;

/// <summary>
/// Sign-in for the browser (#138). The CI stack (docker-compose.e2e.yml) runs OIDC with a mock identity provider that completes the
/// login on its own, so a single navigation to the Web root signs the browser in. That happens once per test run: the resulting cookies
/// are saved as a Playwright storage state which every test context then starts from.
///
/// An anonymous stack (a developer's <c>docker compose up</c> with <c>Auth__AllowAnonymous=true</c>) never redirects to an identity
/// provider; there is nothing to sign in to and contexts start empty. Setting <c>E2E_REQUIRE_AUTH=true</c> (CI does) turns that into a
/// failure, so the suite can never silently run unauthenticated again.
/// </summary>
internal static class E2EAuth
{
    /// <summary>The IdP is reached as <c>http://idp:8080</c> from the containers (network alias) and, by this rule, from Chromium too.</summary>
    internal static readonly string[] BrowserArgs = ["--host-resolver-rules=MAP idp 127.0.0.1"];

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _resolved;
    private static string? _storageStatePath;

    private static bool AuthRequired =>
        string.Equals(Environment.GetEnvironmentVariable("E2E_REQUIRE_AUTH"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Path of the saved signed-in storage state, or null when the stack has no sign-in. Signs in on first use.</summary>
    internal static async Task<string?> StorageStatePathAsync(string baseUrl, IBrowser browser)
    {
        await Gate.WaitAsync();
        try
        {
            if (!_resolved)
            {
                _storageStatePath = await SignInAsync(baseUrl, browser);
                _resolved = true;
            }

            return _storageStatePath;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<string?> SignInAsync(string baseUrl, IBrowser browser)
    {
        var root = new Uri(baseUrl.TrimEnd('/') + "/");
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var leftForIdp = false;
        page.Request += (_, request) =>
        {
            if (Uri.TryCreate(request.Url, UriKind.Absolute, out var u) && u.Host != root.Host)
            {
                leftForIdp = true;
            }
        };

        await page.GotoAsync(root.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });

        if (!leftForIdp)
        {
            return AuthRequired
                ? throw new InvalidOperationException("E2E_REQUIRE_AUTH=true but the stack never redirected to an identity provider: it is not running with OIDC sign-in.")
                : null;
        }

        // The mock IdP auto-completes: authorize -> form_post to /signin-oidc -> redirect back to the app.
        var url = new Uri(page.Url);
        if (url.Host != root.Host || url.AbsolutePath.StartsWith("/auth/failed", StringComparison.OrdinalIgnoreCase) || url.AbsolutePath.StartsWith("/signin-oidc", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Sign-in did not complete: the browser ended on {page.Url}. Check the web/api logs and Tenancy__LegacyOwnerEmail.");
        }

        var path = Path.Combine(Path.GetTempPath(), $"flowledger-e2e-storage-{Guid.NewGuid():N}.json");
        await context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = path });
        return path;
    }
}
