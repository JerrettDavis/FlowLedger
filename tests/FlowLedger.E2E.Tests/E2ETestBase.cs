namespace FlowLedger.E2E.Tests;

using Microsoft.Playwright;

/// <summary>
/// Base class for E2E tests running in CI with headless Chromium.
///
/// CI-Gating: Tests automatically skip if E2E_BASE_URL env var is not set.
/// This ensures E2E tests only run in CI (where the env var is configured)
/// and never on developer machines or in local test runs.
///
/// Configuration:
/// - E2E_BASE_URL: Base URL of the running application (e.g., http://localhost:5002)
/// - Headless mode: Always enabled, never headed
/// - Browser: Chromium
/// </summary>
public class E2ETestBase : IAsyncLifetime
{
    protected IPlaywright? Playwright { get; private set; }
    protected IBrowserContext? Context { get; private set; }
    protected IPage? Page { get; private set; }
    protected string BaseUrl { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        // CI-gating: Skip if E2E_BASE_URL not set
        var baseUrl = Environment.GetEnvironmentVariable("E2E_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("E2E_BASE_URL environment variable not set. Skipping E2E tests. Set this var in CI only.");
        }

        BaseUrl = baseUrl;

        // Install Playwright if needed (idempotent)
        try
        {
            Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("browsers"))
        {
            throw new InvalidOperationException(
                "Playwright browsers not installed. Run 'playwright install' or 'pwsh -Command 'pwsh bin/Debug/net10.0/playwright.ps1' install'",
                ex);
        }

        // Launch headless Chromium (NEVER headed)
        var browser = await Playwright!.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        // Create context and page
        Context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            // Optional: Add custom headers, viewport, etc.
        });
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

    /// <summary>
    /// Navigate to a relative path on the application.
    /// </summary>
    protected async Task NavigateAsync(string path = "/")
    {
        var url = $"{BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
        await Page!.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
    }

    /// <summary>
    /// Wait for the page to be fully loaded.
    /// </summary>
    protected async Task WaitForLoadAsync()
    {
        await Page!.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }
}
