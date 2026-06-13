namespace FlowLedger.E2E.Tests;

using System.Net.Http;
using FluentAssertions;
using Microsoft.Playwright;

/// <summary>
/// Data-asserting E2E tests: verify that real account data renders in the UI.
///
/// This test class exercises the full Web → API auth path introduced in Phase 5.
/// Before Phase 5's fix (adding ApiAuthHeaderHandler), the Web sent no API key and
/// every API call returned 401 — the accounts page rendered no rows regardless of
/// seeded data.  This test would have FAILED on the old code.
///
/// Seed strategy: POST /api/connect + /api/sync via HttpClient with X-Api-Key and
/// X-Tenant-Id headers (mirrors eng/scripts/seed.ps1) so the test is self-contained
/// and deterministic regardless of prior stack state.
///
/// Required environment variables when E2E_BASE_URL is set:
///   E2E_BASE_URL   — Web URL,  e.g. http://localhost:5002
///   E2E_API_URL    — API URL,  e.g. http://localhost:5001  (defaults to :5001 if unset)
///   E2E_API_KEY    — API key   (defaults to dev-local-key-not-for-production)
///   E2E_TENANT_ID  — Tenant    (defaults to 00000000-0000-0000-0000-000000000001)
/// </summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class AccountsDataTests : E2ETestBase
{
    private static readonly string DefaultApiUrl = "http://localhost:5001";
    private static readonly string DefaultApiKey = "dev-local-key-not-for-production";
    private static readonly string DefaultTenantId = "00000000-0000-0000-0000-000000000001";

    private static string ApiUrl =>
        Environment.GetEnvironmentVariable("E2E_API_URL") ?? DefaultApiUrl;

    private static string ApiKey =>
        Environment.GetEnvironmentVariable("E2E_API_KEY") ?? DefaultApiKey;

    private static string TenantId =>
        Environment.GetEnvironmentVariable("E2E_TENANT_ID") ?? DefaultTenantId;

    /// <summary>
    /// Seeds data via the API then asserts that the Accounts page renders at least one
    /// account row with a recognisable account name — proving the Web sends auth headers
    /// and the API returns real data (not 401).
    ///
    /// A 401 from the API causes FlowLedgerApiClient to throw HttpRequestException,
    /// which the Accounts page catches and renders as an error alert instead of rows.
    /// This test explicitly asserts that a seeded account name is visible, which can only
    /// happen when the API call succeeds with a valid API key.
    /// </summary>
    [Fact(DisplayName = "Accounts: seeded data rows appear (proves Web→API auth works)")]
    public async Task Accounts_SeededDataRowsAppear_ProvesAuthWorks()
    {
        if (ShouldSkip)
        {
            return;
        }

        // ── Step 1: Seed via direct API calls (mirrors seed.ps1) ──────────────
        await SeedDataAsync();

        // ── Step 2: Load the Accounts page ──────────────────────────────────
        await NavigateAsync("/accounts");

        // ── Step 3: Assert NO auth-failure error is visible ──────────────────
        // If auth failed (401), Accounts.razor renders:
        //   <MudAlert aria-live="assertive">Failed to load accounts: ...</MudAlert>
        // We verify that error text is NOT present with a short timeout.
        try
        {
            var errorAlert = Page!.Locator("[aria-live='assertive']");
            await errorAlert.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 2000
            });
            var errorText = await errorAlert.First.InnerTextAsync();
            // If we get here, an error was shown — fail with a clear message
            false.Should().BeTrue(
                $"Accounts page showed an error alert (likely 401): '{errorText}'. " +
                "This means the Web is NOT sending X-Api-Key — auth fix regression.");
        }
        catch (TimeoutException)
        {
            // Good — no error alert appeared
        }

        // ── Step 4: Wait for a known seeded account name to appear ────────────
        // The SimulatedFinancialDataProvider seeds accounts with names like
        // "Primary Checking", "High-Yield Savings", "Everyday Visa", "Apex Rewards Card",
        // "Horizon Brokerage", "Home Mortgage".
        // We wait for at least one of these to become visible in the page text.
        // This wait replaces a simple sleep and is robust to Blazor SignalR circuit delays.
        var accountNames = new[]
        {
            "Primary Checking", "High-Yield Savings", "Everyday Visa",
            "Apex Rewards Card", "Horizon Brokerage", "Home Mortgage",
        };

        string? foundName = null;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && foundName is null)
        {
            foreach (var name in accountNames)
            {
                var locator = Page!.GetByText(name, new PageGetByTextOptions { Exact = false });
                if (await locator.CountAsync() > 0 && await locator.First.IsVisibleAsync())
                {
                    foundName = name;
                    break;
                }
            }

            if (foundName is null)
            {
                await Page!.WaitForTimeoutAsync(500);
            }
        }

        foundName.Should().NotBeNull(
            "Expected at least one seeded account name (e.g. 'Primary Checking', 'Horizon Brokerage') to appear " +
            "in the Accounts page within 20 seconds after seeding. " +
            "If null: the Web→API call likely returned 401 (no auth header) or the " +
            "Blazor SignalR circuit did not load data in time.");

        // ── Step 5: Confirm the accounts table grid itself is rendered ────────
        var grid = Page!.Locator("[aria-label='Accounts table']");
        (await grid.CountAsync()).Should().BeGreaterThan(0, "accounts table must be rendered");
        (await grid.IsVisibleAsync()).Should().BeTrue("accounts table must be visible");
    }

    /// <summary>
    /// Seeds data by calling /api/connect then /api/sync with proper auth headers.
    /// Idempotent: 409 on connect is treated as success (already connected).
    /// </summary>
    private static async Task SeedDataAsync()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        http.DefaultRequestHeaders.Add("X-Tenant-Id", TenantId);

        // POST /api/connect — 200 or 409 (already connected) both are OK
        var connectResponse = await http.PostAsync($"{ApiUrl.TrimEnd('/')}/api/connect", null);
        (connectResponse.IsSuccessStatusCode || (int)connectResponse.StatusCode == 409).Should().BeTrue(
            $"POST /api/connect must succeed or return 409 (got {(int)connectResponse.StatusCode}). " +
            "If 401: check E2E_API_KEY matches Api__Key on the api service. " +
            "If unreachable: check E2E_API_URL is correct.");

        // POST /api/sync — pulls demo transactions from SimulatedFinancialDataProvider
        var syncResponse = await http.PostAsync($"{ApiUrl.TrimEnd('/')}/api/sync", null);
        syncResponse.IsSuccessStatusCode.Should().BeTrue(
            $"POST /api/sync must succeed (got {(int)syncResponse.StatusCode})");
    }
}
