namespace FlowLedger.E2E.Tests;

using System.Net.Http;
using FluentAssertions;
using Microsoft.Playwright;

/// <summary>
/// On-demand screenshot capture for documentation and presentations.
///
/// Gated by <c>Category=Screenshots</c> — runs ONLY when explicitly targeted:
///   dotnet test tests/FlowLedger.E2E.Tests --filter Category=Screenshots
///
/// This class is intentionally excluded from the standard CI run (Category!=E2E)
/// and from the default E2E smoke run (Category=E2E).  It is invoked from the
/// docs/screenshots GitHub Actions workflow or on demand locally.
///
/// Required environment variables (same as E2E tests):
///   E2E_BASE_URL   — Web URL,  e.g. http://localhost:5002
///   E2E_API_URL    — API URL,  e.g. http://localhost:5001 (defaults to :5001)
///   E2E_API_KEY    — API key   (defaults to dev-local-key-not-for-production)
///   E2E_TENANT_ID  — Tenant    (defaults to 00000000-0000-0000-0000-000000000001)
///
/// Optional:
///   SCREENSHOT_OUTPUT_DIR — override output directory (default: docs/assets/screenshots
///                           resolved relative to the repo root two levels above the test bin)
/// </summary>
[Trait("Category", "Screenshots")]
public class ScreenshotCaptureTests : E2ETestBase
{
    // ── Environment defaults ──────────────────────────────────────────────────

    private static readonly string DefaultApiUrl = "http://localhost:5001";
    private static readonly string DefaultApiKey = "dev-local-key-not-for-production";
    private static readonly string DefaultTenantId = "00000000-0000-0000-0000-000000000001";

    private static string ApiUrl =>
        Environment.GetEnvironmentVariable("E2E_API_URL") ?? DefaultApiUrl;

    private static string ApiKey =>
        Environment.GetEnvironmentVariable("E2E_API_KEY") ?? DefaultApiKey;

    private static string TenantId =>
        Environment.GetEnvironmentVariable("E2E_TENANT_ID") ?? DefaultTenantId;

    // ── Screenshot tests ──────────────────────────────────────────────────────

    [Fact(DisplayName = "Screenshots: seed data then capture all key pages")]
    public async Task Capture_all_key_pages()
    {
        if (ShouldSkip) { return; }

        // Screenshot-specific page setup: 1440×900 @ 2× for crisp HiDPI PNGs.
        // E2ETestBase creates the page at the default viewport; we resize here.
        await Page!.SetViewportSizeAsync(1440, 900);
        Page.SetDefaultTimeout(30_000);

        var outputDir = ResolveOutputDir();
        Directory.CreateDirectory(outputDir);

        // Step 1 — ensure data is seeded (idempotent: 409 = already connected).
        await SeedDataAsync();

        // Step 2 — warm up the app by navigating to the home page once.
        await NavigateAsync("/");
        await WaitForLoadAsync();
        await Page!.WaitForTimeoutAsync(2_000);

        // Step 3 — capture each page.
        // Dashboard: wait for the SVG chart — "No forecast data" is not acceptable after seeding.
        await CapturePageAsync("/", "dashboard.png", outputDir,
            waitForSelector: "[aria-label='Balance projection chart']");
        await CapturePageAsync("/accounts", "accounts.png", outputDir,
            waitForSelector: "[aria-label='Accounts table']");
        await CapturePageAsync("/transactions", "transactions.png", outputDir,
            waitForSelector: "[aria-label='Transactions table']");
        await CapturePageAsync("/money-plan", "money-plan.png", outputDir,
            waitForSelector: "[aria-label='Money plan spreadsheet']");
        await CapturePageAsync("/recurring-flows", "recurring-flows.png", outputDir,
            waitForSelector: "[aria-label='Recurring flows table']");
        await CapturePageAsync("/forecasts", "forecast.png", outputDir,
            waitForSelector: "[aria-label='Aggregate balance projection chart']");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Navigate to <paramref name="path"/>, wait for <paramref name="waitForSelector"/>
    /// to become visible (required — a timeout is a test failure, not a benign fallback),
    /// assert there is no visible error alert or console/page/HTTP error, then capture the
    /// screenshot.
    /// </summary>
    private async Task CapturePageAsync(
        string path,
        string filename,
        string outputDir,
        string? waitForSelector)
    {
        await NavigateAsync(path);

        // If a selector hint is given, wait for it to appear (data loaded / chart rendered).
        // A timeout here is a FAILURE — the page did not reach the expected ready state.
        if (!string.IsNullOrWhiteSpace(waitForSelector))
        {
            await Page!.WaitForSelectorAsync(waitForSelector, new PageWaitForSelectorOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 25_000,
            });
        }

        // Assert no visible error alert or JSON-error text before capturing the screenshot.
        // If an error IS visible the test fails here with the error text — not a broken image.
        await AssertNoErrorAlertVisible();

        // Assert no console errors, uncaught JS exceptions, or HTTP >= 400 responses were
        // recorded since the page was created.  Inherited from E2ETestBase.
        AssertNoPageErrors();

        // Extra settle time for chart animations to finish rendering.
        await Page!.WaitForTimeoutAsync(1_500);

        var outputPath = Path.Combine(outputDir, filename);

        await Page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = outputPath,
            FullPage = true,
        });
    }

    /// <summary>
    /// Seeds demo data by posting to /api/connect and /api/sync.
    /// Idempotent: 409 on connect is treated as already-connected success.
    /// </summary>
    private static async Task SeedDataAsync()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        http.DefaultRequestHeaders.Add("X-Tenant-Id", TenantId);
        http.Timeout = TimeSpan.FromSeconds(30);

        // POST /api/connect — 200 or 409 (already connected) both OK.
        var connectResponse = await http.PostAsync($"{ApiUrl.TrimEnd('/')}/api/connect", null);
        if (!connectResponse.IsSuccessStatusCode && (int)connectResponse.StatusCode != 409)
        {
            throw new InvalidOperationException(
                $"Screenshot seed: POST /api/connect returned {(int)connectResponse.StatusCode}. " +
                $"Check E2E_API_URL ({ApiUrl}) and E2E_API_KEY.");
        }

        // POST /api/sync — pulls demo transactions from SimulatedFinancialDataProvider.
        var syncResponse = await http.PostAsync($"{ApiUrl.TrimEnd('/')}/api/sync", null);
        if (!syncResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Screenshot seed: POST /api/sync returned {(int)syncResponse.StatusCode}.");
        }
    }

    /// <summary>
    /// Resolves the screenshot output directory.
    ///
    /// Priority:
    ///   1. SCREENSHOT_OUTPUT_DIR env var (absolute path)
    ///   2. docs/assets/screenshots/ relative to the repository root
    ///      (detected by walking up from the test assembly location until FlowLedger.slnx is found
    ///       or until 8 levels are exhausted, falling back to the current working directory).
    /// </summary>
    private static string ResolveOutputDir()
    {
        var envOverride = Environment.GetEnvironmentVariable("SCREENSHOT_OUTPUT_DIR");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            return envOverride;
        }

        // Walk up from the test assembly to find the repo root (contains FlowLedger.slnx).
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(typeof(ScreenshotCaptureTests).Assembly.Location)
            ?? Directory.GetCurrentDirectory());

        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FlowLedger.slnx")))
            {
                return Path.Combine(dir.FullName, "docs", "assets", "screenshots");
            }

            dir = dir.Parent;
        }

        // Fallback: current working directory / docs/assets/screenshots
        return Path.Combine(Directory.GetCurrentDirectory(), "docs", "assets", "screenshots");
    }
}
