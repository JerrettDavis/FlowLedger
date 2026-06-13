namespace FlowLedger.E2E.Tests;

using System.Net.Http;
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
public class ScreenshotCaptureTests : IAsyncLifetime
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

    // ── Playwright state ──────────────────────────────────────────────────────

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private string _baseUrl = string.Empty;
    private bool _shouldSkip;
    private string _outputDir = string.Empty;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("E2E_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _shouldSkip = true;
            return;
        }

        _baseUrl = baseUrl;
        _outputDir = ResolveOutputDir();

        Directory.CreateDirectory(_outputDir);

        _playwright = await Microsoft.Playwright.Playwright.CreateAsync();

        // Always headless — never steal the user's mouse/keyboard focus.
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
        });

        // 1440×900 @ 2× scale produces crisp 2880×1800 PNGs — good for Retina / HiDPI docs.
        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            DeviceScaleFactor = 2,
        });

        _page = await _context.NewPageAsync();

        // Increase default Playwright action timeout for slow Blazor SignalR circuits.
        _page.SetDefaultTimeout(30_000);
    }

    public async Task DisposeAsync()
    {
        if (_page is not null) { await _page.CloseAsync(); }
        if (_context is not null) { await _context.CloseAsync(); }
        if (_browser is not null) { await _browser.CloseAsync(); }
        _playwright?.Dispose();
    }

    // ── Screenshot tests ──────────────────────────────────────────────────────

    [Fact(DisplayName = "Screenshots: seed data then capture all key pages")]
    public async Task Capture_all_key_pages()
    {
        if (_shouldSkip) { return; }

        // Step 1 — ensure data is seeded (idempotent: 409 = already connected).
        await SeedDataAsync();

        // Step 2 — warm up the app by navigating to the home page once.
        await NavigateAndWaitAsync("/");
        await _page!.WaitForTimeoutAsync(2_000);

        // Step 3 — capture each page.
        // Dashboard: wait for either the chart SVG or the "no data" fallback text.
        await CapturePageWithFallbackSelectorAsync("/", "dashboard.png",
            primarySelector: "[aria-label='Balance projection chart']",
            fallbackSelector: "[aria-label='Forecast Low']");
        await CapturePageAsync("/accounts", "accounts.png", waitForSelector: "[aria-label='Accounts table']");
        await CapturePageAsync("/transactions", "transactions.png", waitForSelector: "[aria-label='Transactions table']");
        await CapturePageAsync("/money-plan", "money-plan.png", waitForSelector: null);
        await CapturePageAsync("/recurring-flows", "recurring-flows.png", waitForSelector: null);
        await CapturePageAsync("/forecasts", "forecast.png", waitForSelector: null);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task CapturePageWithFallbackSelectorAsync(
        string path,
        string filename,
        string primarySelector,
        string fallbackSelector)
    {
        await NavigateAndWaitAsync(path);

        // Try primary selector first; if it times out, try the fallback.
        var found = false;
        foreach (var sel in new[] { primarySelector, fallbackSelector })
        {
            try
            {
                await _page!.WaitForSelectorAsync(sel, new PageWaitForSelectorOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 20_000,
                });
                found = true;
                break;
            }
            catch (TimeoutException) { }
        }

        if (!found)
        {
            // Give the page extra time to settle.
            await _page!.WaitForTimeoutAsync(5_000);
        }

        await _page!.WaitForTimeoutAsync(1_500);

        await _page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Combine(_outputDir, filename),
            FullPage = true,
        });
    }

    private async Task CapturePageAsync(
        string path,
        string filename,
        string? waitForSelector)
    {
        await NavigateAndWaitAsync(path);

        // If a selector hint is given, wait for it to appear (data loaded / chart rendered).
        if (!string.IsNullOrWhiteSpace(waitForSelector))
        {
            try
            {
                await _page!.WaitForSelectorAsync(waitForSelector, new PageWaitForSelectorOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 25_000,
                });
            }
            catch (TimeoutException)
            {
                // Tolerate — capture whatever is on screen; the test won't fail on a missing
                // optional selector (e.g. the chart vs. "no data" fallback).
            }
        }

        // Extra settle time for chart animations to finish rendering.
        await _page!.WaitForTimeoutAsync(1_500);

        var outputPath = Path.Combine(_outputDir, filename);

        await _page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = outputPath,
            FullPage = true,
        });
    }

    private async Task NavigateAndWaitAsync(string path)
    {
        var url = $"{_baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
        await _page!.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 30_000,
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
    ///      (detected by walking up from the test assembly location until CLAUDE.md is found
    ///       or until 6 levels are exhausted, falling back to the assembly directory).
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
