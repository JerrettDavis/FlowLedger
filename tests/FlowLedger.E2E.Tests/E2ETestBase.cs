namespace FlowLedger.E2E.Tests;

using System.Collections.Concurrent;
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
        // Add other known-benign patterns here, e.g. third-party analytics:
        // "analytics.example.com",
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
    /// </summary>
    protected async Task AssertNoErrorAlertVisible()
    {
        if (Page is null)
        {
            return;
        }

        // MudAlert with aria-live="assertive" is the standard error alert pattern used
        // throughout FlowLedger Blazor pages (e.g. "Failed to load accounts: ...").
        var assertiveLocator = Page.Locator("[aria-live='assertive']");
        try
        {
            await assertiveLocator.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 2000,
            });

            // If we reach here, an error alert IS visible — fail with its text.
            var alertText = await assertiveLocator.First.InnerTextAsync();
            false.Should().BeTrue(
                $"Expected no error alert on the page, but found a visible " +
                $"[aria-live='assertive'] element with text: '{alertText}'");
        }
        catch (PlaywrightException)
        {
            // Good — no assertive error alert appeared within the probe window.
            // Microsoft.Playwright.PlaywrightException is thrown by WaitForAsync on timeout.
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
            catch (PlaywrightException)
            {
                // Ignore transient Playwright errors during the retry loop only.
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
