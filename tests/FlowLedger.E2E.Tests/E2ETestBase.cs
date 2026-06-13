namespace FlowLedger.E2E.Tests;

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
/// </summary>
public class E2ETestBase : IAsyncLifetime
{
    protected IPlaywright? Playwright { get; private set; }
    protected IBrowserContext? Context { get; private set; }
    protected IPage? Page { get; private set; }
    protected string BaseUrl { get; private set; } = string.Empty;

    /// <summary>
    /// Returns <c>true</c> when E2E_BASE_URL is not set.
    /// Each [Fact] method should guard with: <c>if (ShouldSkip) return;</c>
    /// </summary>
    protected bool ShouldSkip { get; private set; }

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
}
