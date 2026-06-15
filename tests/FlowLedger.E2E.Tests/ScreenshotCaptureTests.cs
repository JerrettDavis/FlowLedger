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
///
/// Naming convention:
///   Light mode: &lt;page&gt;.png          (e.g. dashboard.png)
///   Dark mode:  &lt;page&gt;-dark.png     (e.g. dashboard-dark.png)
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

    // ── Page definitions ──────────────────────────────────────────────────────

    /// <summary>
    /// Pages to capture: (route, base filename without extension, required aria selector).
    /// Both light and dark variants are captured for each entry.
    /// </summary>
    private static readonly (string Path, string BaseName, string WaitForSelector)[] Pages =
    [
        ("/",               "dashboard",       "[aria-label='Balance projection chart']"),
        ("/accounts",       "accounts",        "[aria-label='Accounts table']"),
        ("/transactions",   "transactions",    "[aria-label='Transactions table']"),
        ("/money-plan",     "money-plan",      "[aria-label='Money plan spreadsheet']"),
        ("/recurring-flows","recurring-flows", "[aria-label='Recurring flows table']"),
        ("/forecasts",      "forecast",        "[aria-label='Aggregate balance projection chart']"),
    ];

    // ── Screenshot tests ──────────────────────────────────────────────────────

    [Fact(DisplayName = "Screenshots: seed data then capture all key pages (light + dark)")]
    public async Task Capture_all_key_pages()
    {
        if (ShouldSkip) { return; }

        var outputDir = ResolveOutputDir();
        Directory.CreateDirectory(outputDir);

        // Step 1 — ensure data is seeded (idempotent: 409 = already connected).
        await SeedDataAsync();

        // Step 2 — capture light mode (default — no color-scheme override).
        await CaptureAllPagesAsync(outputDir, darkMode: false);

        // Step 3 — capture dark mode.
        // The app reads prefers-color-scheme via FlowLedgerTheme.getSystemPrefersDark()
        // on first render (OnAfterRenderAsync) and passes it to ThemeService.Initialize().
        // MudThemeProvider also has ObserveSystemDarkModeChange="true".
        // EmulateMediaAsync on a fresh context is therefore the most reliable trigger:
        // it makes matchMedia('(prefers-color-scheme: dark)') return true throughout the
        // session without needing to click the in-app toggle or deal with Blazor circuit
        // timing issues.
        await CaptureAllPagesAsync(outputDir, darkMode: true);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Captures every page in <see cref="Pages"/> using a fresh browser context so the
    /// color-scheme emulation is clean for each mode.
    /// </summary>
    private async Task CaptureAllPagesAsync(string outputDir, bool darkMode)
    {
        var colorScheme = darkMode ? ColorScheme.Dark : ColorScheme.Light;
        var suffix = darkMode ? "-dark" : string.Empty;

        // Create a dedicated context for this color-scheme pass so localStorage from a
        // previous pass does not bleed through (each context starts with a clean slate).
        await using var context = await CreateColorSchemeContextAsync(colorScheme);
        var page = await context.NewPageAsync();

        // 1440×900 @ default DPR for crisp, consistent documentation screenshots.
        await page.SetViewportSizeAsync(1440, 900);
        page.SetDefaultTimeout(30_000);

        // Warm up: navigate to the home page once to let the Blazor circuit initialise
        // and the theme preference to be read from matchMedia.
        var warmUpUrl = $"{BaseUrl.TrimEnd('/')}/";
        await page.GotoAsync(warmUpUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForTimeoutAsync(2_000);

        foreach (var (path, baseName, waitForSelector) in Pages)
        {
            var filename = $"{baseName}{suffix}.png";
            await CapturePageAsync(page, path, filename, outputDir, waitForSelector);
        }
    }

    /// <summary>
    /// Creates a new <see cref="IBrowserContext"/> with the given color-scheme emulated.
    /// The caller is responsible for disposing the returned context.
    /// </summary>
    private async Task<IBrowserContext> CreateColorSchemeContextAsync(ColorScheme colorScheme)
    {
        // Playwright is initialised by E2ETestBase.InitializeAsync(); the browser is
        // the same Chromium instance — we just open an additional isolated context.
        var browser = Context!.Browser
            ?? throw new InvalidOperationException(
                "E2ETestBase browser is not available. Ensure InitializeAsync ran successfully.");

        return await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = colorScheme,
        });
    }

    /// <summary>
    /// Navigate to <paramref name="path"/>, wait for <paramref name="waitForSelector"/>
    /// to become visible (required — a timeout is a test failure, not a benign fallback),
    /// assert there is no visible error alert or console/page/HTTP error, then capture the
    /// screenshot.
    /// </summary>
    private async Task CapturePageAsync(
        IPage page,
        string path,
        string filename,
        string outputDir,
        string? waitForSelector)
    {
        var url = $"{BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // If a selector hint is given, wait for it to appear (data loaded / chart rendered).
        // A timeout here is a FAILURE — the page did not reach the expected ready state.
        if (!string.IsNullOrWhiteSpace(waitForSelector))
        {
            await page.WaitForSelectorAsync(waitForSelector, new PageWaitForSelectorOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 25_000,
            });
        }

        // Assert no visible error alert or JSON-error text before capturing the screenshot.
        // If an error IS visible the test fails here with the error text — not a broken image.
        await AssertNoErrorAlertVisibleOnPage(page);

        // Extra settle time for chart animations to finish rendering.
        await page.WaitForTimeoutAsync(1_500);

        var outputPath = Path.Combine(outputDir, filename);

        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = outputPath,
            FullPage = true,
        });
    }

    /// <summary>
    /// Page-scoped variant of <see cref="E2ETestBase.AssertNoErrorAlertVisible"/> that
    /// operates on an arbitrary <see cref="IPage"/> instance instead of the base-class
    /// <c>Page</c> property.  Required because screenshot capture opens additional
    /// browser contexts with their own page instances.
    /// </summary>
    private static async Task AssertNoErrorAlertVisibleOnPage(IPage page)
    {
        var assertiveLocator = page.Locator("[aria-live='assertive']");

        const int pollIntervalMs = 250;
        const int maxPollMs = 1500;
        var deadline = DateTime.UtcNow.AddMilliseconds(maxPollMs);

        var appErrorPhrases = new[]
        {
            "Failed to load",
            "Couldn't load",
            "server returned",
            "An error occurred",
            "An unhandled exception",
            "HTTP 500",
        };

        while (true)
        {
            var count = await assertiveLocator.CountAsync();
            if (count > 0)
            {
                for (var i = 0; i < count; i++)
                {
                    var el = assertiveLocator.Nth(i);
                    if (!await el.IsVisibleAsync())
                    {
                        continue;
                    }

                    var alertText = await el.InnerTextAsync();
                    if (appErrorPhrases.Any(p =>
                            alertText.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        false.Should().BeTrue(
                            $"Expected no application error alert on the page, but found a " +
                            $"visible [aria-live='assertive'] element with text: '{alertText}'");
                        return;
                    }
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            await page.WaitForTimeoutAsync(pollIntervalMs);
        }

        var jsonErrorPatterns = new[]
        {
            "invalid start of a value",
            "The JSON value could not be converted",
            "JSException",
            "JsonException",
            "An unhandled exception occurred",
            "HTTP 500",
        };

        foreach (var pattern in jsonErrorPatterns)
        {
            var locator = page.GetByText(pattern, new PageGetByTextOptions { Exact = false });
            var count = await locator.CountAsync();
            if (count > 0 && await locator.First.IsVisibleAsync())
            {
                var text = await locator.First.InnerTextAsync();
                false.Should().BeTrue(
                    $"Expected no error text on the page, but found visible text matching " +
                    $"'{pattern}': '{text}'");
            }
        }
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
