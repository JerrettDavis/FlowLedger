namespace FlowLedger.E2E.Tests;

using System.Collections.Concurrent;
using System.Net.Http;
using FluentAssertions;
using Microsoft.Playwright;

/// <summary>
/// Base class for E2E tests running in CI with headless Chromium.
///
/// CI-Gating: When E2E_BASE_URL is not set, <see cref="ShouldSkip"/> returns <c>true</c>
/// and each test method exits early via <c>if (ShouldSkip) return;</c>.  No browser is
/// launched, and the test is recorded as Passed (0 failures) by xUnit.
///
/// In CI, E2E_BASE_URL is set to the composed Web URL (e.g. http://localhost:5002) and
/// tests run for real against a live headless Chromium instance.
///
/// Headless is always <c>true</c> — headed mode must never be used.
///
/// Error-detection harness: every test inherits console-error, JS-exception, and
/// HTTP-error listeners. Call <see cref="AssertNoPageErrors"/> at end of a test (or rely
/// on teardown) to fail on any recorded error. Call <see cref="AssertNoErrorAlertVisible"/>
/// to assert no visible MudBlazor error alert / JSON-error text is on screen.
/// </summary>
public class E2ETestBase : IAsyncLifetime
{
    // ── Playwright state ──────────────────────────────────────────────────────

    protected IPlaywright? Playwright { get; private set; }
    protected IBrowserContext? Context { get; private set; }
    protected IPage? Page { get; private set; }
    protected string BaseUrl { get; private set; } = string.Empty;

    /// <summary>
    /// Returns <c>true</c> when E2E_BASE_URL is not set.
    /// Each [Fact] method should guard with: <c>if (ShouldSkip) return;</c>
    /// </summary>
    protected bool ShouldSkip { get; private set; }

    // ── Error-capture state ───────────────────────────────────────────────────

    /// <summary>Console messages of type "error" or "warning" captured from the page.</summary>
    private readonly ConcurrentBag<string> _consoleErrors = new();

    /// <summary>Uncaught JS exceptions (Page.PageError events).</summary>
    private readonly ConcurrentBag<string> _pageErrors = new();

    /// <summary>HTTP responses with status >= 400, and request-failed events.</summary>
    private readonly ConcurrentBag<string> _httpErrors = new();

    /// <summary>
    /// URL substrings that are benign and should NOT cause a test failure even when they
    /// produce a 4xx/5xx response or a console error.  Extend as needed for known noise.
    /// </summary>
    private static readonly string[] BenignUrlPatterns =
    [
        "favicon.ico",
        "favicon.png",
        // Blazor circuit teardown: triggered by NavigateAsync page transitions and at end of tests.
        // The browser aborts the in-flight disconnect request when the page context closes —
        // this is normal and must not cause a test failure.
        "_blazor/disconnect",
        "_blazor?id=",
    ];

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("E2E_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            // E2E_BASE_URL not set — record skip flag, do NOT launch browser.
            // Tests guard with `if (ShouldSkip) return;` to exit cleanly (0 failures).
            ShouldSkip = true;
            return;
        }

        ShouldSkip = false;
        BaseUrl = baseUrl;

        // Ensure data is seeded before every test class so that tests are independent
        // of execution order. Seeding is idempotent: connect returns 409 if already
        // connected, and sync is always safe to call repeatedly.
        await EnsureSeededAsync();

        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();

        // Always headless — never set Headless = false
        var browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        Context = await browser.NewContextAsync();
        Page = await Context.NewPageAsync();

        // ── Attach error-detection listeners ──────────────────────────────────

        // 1. Console messages: capture "error" and "warning" level entries.
        Page.Console += (_, msg) =>
        {
            if (msg.Type is "error" or "warning")
            {
                _consoleErrors.Add($"[console:{msg.Type}] {msg.Text}");
            }
        };

        // 2. Uncaught JS / Blazor exceptions (fires for unhandled Promise rejections too).
        Page.PageError += (_, error) =>
        {
            _pageErrors.Add($"[page-error] {error}");
        };

        // 3. HTTP responses: record anything >= 400 that is not in the benign allowlist.
        Page.Response += (_, response) =>
        {
            if (response.Status >= 400 && !IsBenignUrl(response.Url))
            {
                _httpErrors.Add($"[http-{response.Status}] {response.Url}");
            }
        };

        // 4. Network-level request failures (DNS, connection refused, SSL, etc.).
        Page.RequestFailed += (_, request) =>
        {
            if (!IsBenignUrl(request.Url))
            {
                _httpErrors.Add($"[request-failed] {request.Url} — {request.Failure}");
            }
        };
    }

    public async Task DisposeAsync()
    {
        if (Page is not null)
        {
            await Page.CloseAsync();
        }

        if (Context is not null)
        {
            await Context.CloseAsync();
        }

        Playwright?.Dispose();
    }

    // ── Error-assertion helpers ───────────────────────────────────────────────

    /// <summary>
    /// Fails the test if any console errors, uncaught JS exceptions, or HTTP >= 400
    /// responses were recorded since the page was created.  Call this at the end of any
    /// test that should be clean.  It is NOT called automatically in teardown because
    /// xUnit teardown exceptions do not reliably surface as test failures — call it
    /// explicitly at the end of each test body (or at least in the key data tests).
    /// </summary>
    protected void AssertNoPageErrors()
    {
        var all = new List<string>();
        all.AddRange(_consoleErrors);
        all.AddRange(_pageErrors);
        all.AddRange(_httpErrors);

        all.Should().BeEmpty(
            "no browser console errors, uncaught JS exceptions, or HTTP >= 400 responses " +
            "should be present, but found:\n" + string.Join("\n", all));
    }

    /// <summary>
    /// Asserts that no visible MudBlazor error alert, aria-live assertive region, or
    /// JSON/exception error text is currently displayed on the page.
    ///
    /// If an error alert IS visible this method fails immediately with the alert text,
    /// which surfaces the exact error message the UI is showing rather than a vague
    /// "element not found" failure.
    ///
    /// Implementation note: this method deliberately avoids WaitForAsync + catch for the
    /// "element absent" check.  WaitForAsync throws either PlaywrightException or
    /// TimeoutException depending on the Microsoft.Playwright .NET version, and catching the
    /// wrong type silently misses the exception and propagates it as a test failure even on
    /// a perfectly clean page.  Instead we use CountAsync() (never throws) to probe element
    /// presence, which is both version-proof and fast on the happy path.
    /// </summary>
    protected async Task AssertNoErrorAlertVisible()
    {
        if (Page is null)
        {
            return;
        }

        // MudAlert with aria-live="assertive" is the standard error alert pattern used
        // throughout FlowLedger Blazor pages (e.g. "Failed to load accounts: ...").
        // Poll briefly to allow any async error alerts to appear after navigation/load.
        // On a clean page, CountAsync() returns 0 immediately — no hard wait is incurred.
        //
        // Note: domain-level warning alerts (e.g. "Overdraft Risk Detected") also use
        // aria-live="assertive" via MudAlert — these are NOT system errors and must not
        // trigger a test failure.  Only text that matches application-error patterns is
        // treated as a failure (see _appErrorPhrases below).
        var assertiveLocator = Page.Locator("[aria-live='assertive']");

        const int pollIntervalMs = 250;
        const int maxPollMs = 1500;
        var deadline = DateTime.UtcNow.AddMilliseconds(maxPollMs);

        // Phrases that indicate an application/infrastructure error, not domain data.
        // Keep this list narrow — only unmistakeable error messages from the UI layer.
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
                    // Only fail when the alert text looks like an app/infra error.
                    // Domain warnings (e.g. "Overdraft Risk Detected") are acceptable.
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

            // No visible error alert yet.  If the poll window has elapsed, we're clean.
            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            await Page.WaitForTimeoutAsync(pollIntervalMs);
        }

        // Also scan for common JSON/Blazor error strings anywhere in visible text.
        // These appear when Blazor receives a non-JSON response (e.g. HTML error page)
        // or when JSON deserialization fails ("The JSON value could not be converted",
        // "invalid start of a value", etc.).
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
            var locator = Page.GetByText(pattern, new PageGetByTextOptions { Exact = false });
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

    // ── Seed helpers ──────────────────────────────────────────────────────────

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
    /// Seeds demo data via /api/connect + /api/sync so all E2E tests have accounts and
    /// transactions regardless of execution order.  Idempotent: a 409 on connect (already
    /// connected) and a repeated sync are both safe.
    /// </summary>
    private static async Task EnsureSeededAsync()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        http.DefaultRequestHeaders.Add("X-Tenant-Id", TenantId);

        var apiBase = ApiUrl.TrimEnd('/');

        // Connect — 200 or 409 (already connected) are both acceptable.
        var connectResponse = await http.PostAsync($"{apiBase}/api/connect", content: null);
        (connectResponse.IsSuccessStatusCode || (int)connectResponse.StatusCode == 409)
            .Should().BeTrue(
                $"POST /api/connect must succeed or return 409, got {(int)connectResponse.StatusCode}.");

        // Sync — pulls demo data from SimulatedFinancialDataProvider.
        var syncResponse = await http.PostAsync($"{apiBase}/api/sync", content: null);
        syncResponse.IsSuccessStatusCode.Should().BeTrue(
            $"POST /api/sync must succeed, got {(int)syncResponse.StatusCode}.");
    }

    // ── Navigation helpers ────────────────────────────────────────────────────

    /// <summary>Navigate to a relative path on the application.</summary>
    protected async Task NavigateAsync(string path = "/")
    {
        var url = $"{BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
        await Page!.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
    }

    /// <summary>Wait for the page to reach network idle state.</summary>
    protected async Task WaitForLoadAsync()
    {
        await Page!.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>
    /// Waits for a MudDataGrid to contain at least one data row (gridcell), then returns
    /// the count of gridcells found.
    ///
    /// Background: Blazor InteractiveServer pages use a SignalR circuit that is NOT tracked
    /// by Playwright's NetworkIdle (WebSockets are transparent to it).  The circuit fires
    /// OnInitializedAsync, which triggers an API call whose response re-renders the grid —
    /// all after NetworkIdle has already resolved.  In Docker, this interactive-render cycle
    /// can add 10–20 s on top of the static pre-render phase.
    ///
    /// This helper compensates by:
    ///   1. Re-waiting for NetworkIdle to flush any in-flight HTTP calls the circuit triggered.
    ///   2. Waiting for the grid container to become visible (covers SSR pre-render gap).
    ///   3. Polling for tbody td elements inside the container with a generous 30 s budget
    ///      that absorbs the full interactive-render + API-round-trip latency seen in Docker.
    ///      Note: MudBlazor 9 renders MudDataGrid rows as standard HTML table elements
    ///      (tbody/tr/td), not as [role='gridcell'] elements.
    ///
    /// The poll uses CountAsync() (never throws) following the same robust, non-exception-based
    /// style as AssertNoErrorAlertVisible so Playwright version differences do not matter.
    ///
    /// The method does NOT assert — call .Should().BeGreaterThan(0) on the return value so
    /// each test controls its own failure message.
    /// </summary>
    /// <param name="gridContainerSelector">
    ///   CSS attribute selector for the MudDataGrid wrapper, e.g.
    ///   "[aria-label='Transactions table']".
    /// </param>
    /// <param name="gridCellTimeoutMs">
    ///   How long to poll for gridcells before giving up.  Default 30 000 ms, which
    ///   comfortably covers the Docker interactive-render + data-fetch round-trip.
    /// </param>
    protected async Task<int> WaitForGridDataAsync(
        string gridContainerSelector,
        int gridCellTimeoutMs = 30_000)
    {
        // Step 1: re-wait for NetworkIdle so any HTTP calls the Blazor circuit already
        // triggered (but that landed just after the first NetworkIdle) are settled.
        await Page!.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Step 2: wait for the grid container itself to be in the DOM and visible.
        // Use CountAsync() polling (never throws) consistent with our error-check pattern.
        var container = Page.Locator(gridContainerSelector);
        const int containerPollMs = 250;
        const int containerTimeoutMs = 15_000;
        var containerDeadline = DateTime.UtcNow.AddMilliseconds(containerTimeoutMs);
        while (true)
        {
            var containerCount = await container.CountAsync();
            if (containerCount > 0 && await container.First.IsVisibleAsync())
            {
                break;
            }

            if (DateTime.UtcNow >= containerDeadline)
            {
                // Container itself never appeared — return 0 so the caller's assertion fails
                // with a meaningful message rather than throwing here.
                return 0;
            }

            await Page.WaitForTimeoutAsync(containerPollMs);
        }

        // Step 3: poll for data cells inside the container with the generous Docker-friendly
        // timeout.  CountAsync() is used throughout to avoid throwing on absent elements.
        //
        // MudBlazor 9 renders MudDataGrid rows as standard <tbody><tr><td> elements.
        // The <td> cells are inside a <tbody> which appears only when data rows exist —
        // header <th> cells live in <thead> and are not counted here.
        var gridCells = container.Locator("tbody td");
        const int cellPollMs = 500;
        var cellDeadline = DateTime.UtcNow.AddMilliseconds(gridCellTimeoutMs);
        var cellCount = 0;
        while (DateTime.UtcNow < cellDeadline && cellCount == 0)
        {
            cellCount = await gridCells.CountAsync();
            if (cellCount == 0)
            {
                await Page.WaitForTimeoutAsync(cellPollMs);
            }
        }

        return cellCount;
    }

    /// <summary>
    /// Click a button and wait for a text to become visible, retrying the click
    /// until the condition is met or the timeout expires.
    ///
    /// Blazor Web pre-renders components server-side (SSR), so <c>NetworkIdle</c> fires on the
    /// static HTML before <c>blazor.web.js</c> has established the SignalR circuit and wired up
    /// interactive event handlers. Playwright's <c>NetworkIdle</c> does not track WebSockets,
    /// so it fires before the Blazor Server SignalR circuit is live. Clicking immediately after
    /// <c>NetworkIdle</c> may fire before the circuit wires up interactive event handlers, so
    /// the click is silently ignored by the browser.
    ///
    /// This helper retries the click at short intervals until <paramref name="expectedText"/>
    /// becomes visible on the page, which confirms the interactive handler fired.
    /// </summary>
    protected async Task ClickUntilVisibleAsync(
        ILocator clickTarget,
        string expectedText,
        int intervalMs = 500,
        int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var visible = await Page!.GetByText(expectedText).IsVisibleAsync();
            if (visible)
            {
                return;
            }

            try
            {
                await clickTarget.ClickAsync();
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                // Ignore transient Playwright errors during the retry loop only.
                // Both PlaywrightException and TimeoutException can be thrown by ClickAsync
                // depending on the Microsoft.Playwright .NET version and timing conditions.
                // Non-Playwright exceptions (e.g. ObjectDisposedException, NullReferenceException)
                // are not caught here and will propagate to the test as real failures.
            }

            await Page!.WaitForTimeoutAsync(intervalMs);
        }

        // Final assertion — let the caller's WaitForAsync produce a clear error
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static bool IsBenignUrl(string url)
    {
        foreach (var pattern in BenignUrlPatterns)
        {
            if (url.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
