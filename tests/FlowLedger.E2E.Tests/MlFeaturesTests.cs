namespace FlowLedger.E2E.Tests;

using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;

/// <summary>
/// E2E coverage for the ML enhancements: categorizer suggestions, anomaly flags, forecast bands and "Ask in plain English".
/// Each test seeds its own data through the API (X-Api-Key + X-Tenant-Id, like seed.ps1) and asserts what the browser shows.
/// Run ONLY against a disposable stack: the tests create accounts and transactions.
/// </summary>
[Collection("E2E Collection")]
[Trait("Category", "E2E")]
public class MlFeaturesTests : E2ETestBase
{
    private static string ApiUrl => (Environment.GetEnvironmentVariable("E2E_API_URL") ?? "http://localhost:5001").TrimEnd('/');
    private static string ApiKey => Environment.GetEnvironmentVariable("E2E_API_KEY") ?? "dev-local-key-not-for-production";
    private static string TenantId => Environment.GetEnvironmentVariable("E2E_TENANT_ID") ?? "00000000-0000-0000-0000-000000000001";

    private static HttpClient Api()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        http.DefaultRequestHeaders.Add("X-Tenant-Id", TenantId);
        return http;
    }

    private static string Today(int daysAgo = 0) => DateOnly.FromDateTime(DateTime.Now).AddDays(-daysAgo).ToString("yyyy-MM-dd");

    private static async Task<Guid> CreateAccountAsync(HttpClient http, string name)
    {
        var r = await http.PostAsJsonAsync($"{ApiUrl}/api/accounts", new { name, accountType = "Checking", balanceAmount = 10000m, currency = "USD" });
        r.IsSuccessStatusCode.Should().BeTrue(await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateCategoryAsync(HttpClient http, string path)
    {
        var r = await http.PostAsJsonAsync($"{ApiUrl}/api/categories", new { path, displayName = path.Split('/').Last(), parentId = (Guid?)null });
        r.IsSuccessStatusCode.Should().BeTrue(await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task SpendAsync(HttpClient http, Guid account, decimal amount, string merchant, int daysAgo, Guid? category = null)
    {
        var r = await http.PostAsJsonAsync($"{ApiUrl}/api/transactions", new
        {
            accountId = account, amount, currency = "USD", direction = "Debit", description = merchant,
            effectiveDate = Today(daysAgo), postedDate = Today(daysAgo), categoryId = category, merchantName = category.HasValue ? merchant : null, notes = (string?)null,
        });
        r.IsSuccessStatusCode.Should().BeTrue(await r.Content.ReadAsStringAsync());
    }

    private static string Unique(string s) => $"{s} {Guid.NewGuid().ToString("N")[..4].ToUpperInvariant()}";

    [Fact(DisplayName = "Spending: the learned categorizer offers a one-click suggestion that can be accepted")]
    public async Task Spending_ShowsCategorySuggestion_AndAcceptingCategorizes()
    {
        if (ShouldSkip) { return; }

        using var http = Api();
        var acct = await CreateAccountAsync(http, Unique("E2E Cat"));
        // Letters only and unique per run: the model ignores digits, and earlier runs must not leave conflicting labels behind.
        var tag = new string(Enumerable.Range(0, 5).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());
        var diningPath = Unique("E2E Dining").Replace(" ", "");
        var dining = await CreateCategoryAsync(http, diningPath);
        var hardware = await CreateCategoryAsync(http, Unique("E2E Hardware").Replace(" ", ""));
        // Labeled merchants share a type word ("Taqueria" / "Fastener Warehouse") but each has its own random brand, so the
        // deterministic per-merchant rules can't cover the target: only the learned model can generalise to a brand-new name.
        static string Brand() => new(Enumerable.Range(0, 6).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());
        // The shared type words are random per run too, so categories left behind by earlier runs cannot split the model's vote.
        var diningWord = Brand() + "eria";
        var hardwareWord = Brand() + "yard";
        for (var i = 0; i < 15; i++)
        {
            await SpendAsync(http, acct, 20m, $"{Brand()} {diningWord}", 3 + i, dining);
            await SpendAsync(http, acct, 85m, $"{Brand()} {hardwareWord}", 3 + i, hardware);
        }

        var target = $"{Brand()} {diningWord}";
        await SpendAsync(http, acct, 22m, target, 1);
        (await http.PostAsync($"{ApiUrl}/api/categorization/run", null)).IsSuccessStatusCode.Should().BeTrue();

        await NavigateAsync("/spending");
        await WaitForLoadAsync();
        // The table lists the largest rows first and is capped, so narrow it to the uncategorized group where the suggestion lives.
        await Page!.GetByRole(AriaRole.Button, new() { Name = "Uncategorized", Exact = true }).First.ClickAsync();
        await WaitForLoadAsync();
        var row = Page!.Locator("tr", new() { HasText = target });
        await row.First.WaitForAsync(new() { Timeout = 30000 });
        var chip = row.GetByText("Suggested:");
        await chip.First.WaitForAsync(new() { Timeout = 15000 });
        (await chip.First.InnerTextAsync()).Should().Contain("Dining");

        await chip.First.ClickAsync();

        // Accepting recategorizes, so the row leaves the Uncategorized group; confirm through the data itself.
        await Page.Locator("tr", new() { HasText = target }).First
            .WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 20000 });
        var query = await http.PostAsJsonAsync($"{ApiUrl}/api/query/run",
            new { query = $"transactions | where description contains \"{target}\" | project category" });
        query.IsSuccessStatusCode.Should().BeTrue();
        var rows = (await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows");
        rows.GetArrayLength().Should().Be(1);
        rows[0][0].GetString().Should().Be(diningPath, "accepting the suggestion must file the transaction under the suggested category");
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "Accounts: an account's type can be corrected by hand and is pinned against sync")]
    public async Task Accounts_TypeCanBeCorrectedByHand_AndIsPinned()
    {
        if (ShouldSkip) { return; }

        using var http = Api();
        var name = Unique("E2E Chase Card");
        var id = await CreateAccountAsync(http, name);   // created as Checking, like the misclassified card

        await NavigateAsync("/accounts");
        await WaitForLoadAsync();
        await Page!.GetByRole(AriaRole.Button, new() { Name = $"Edit account {name}" }).ClickAsync();
        await Page.GetByRole(AriaRole.Combobox, new() { Name = "Account type" }).ClickAsync();
        await Page.GetByRole(AriaRole.Option, new() { Name = "CreditCard", Exact = true }).ClickAsync();
        await Page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Page.Locator(".mud-dialog").WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 20000 });

        var accounts = await http.GetFromJsonAsync<JsonElement>($"{ApiUrl}/api/accounts");
        var mine = accounts.EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == id);
        mine.GetProperty("accountType").GetString().Should().Be("CreditCard");
        mine.GetProperty("typeLockedByUser").GetBoolean().Should().BeTrue("a manual correction must not be overwritten by the next sync");

        // A provider sync must not change it back.
        (await http.PostAsync($"{ApiUrl}/api/sync", null)).IsSuccessStatusCode.Should().BeTrue();
        accounts = await http.GetFromJsonAsync<JsonElement>($"{ApiUrl}/api/accounts");
        accounts.EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == id)
            .GetProperty("accountType").GetString().Should().Be("CreditCard");

        await NavigateAsync("/accounts");
        await WaitForLoadAsync();
        (await Page.Locator("tr", new() { HasText = name }).First.InnerTextAsync()).Should().Contain("CreditCard");
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "Dashboard: a subscription price increase is flagged under Needs a Look")]
    public async Task Dashboard_FlagsSubscriptionPriceIncrease()
    {
        if (ShouldSkip) { return; }

        using var http = Api();
        var acct = await CreateAccountAsync(http, Unique("E2E Subs"));
        var merchant = Unique("Streamly Plus").Replace("0", "A").Replace("1", "B").Replace("2", "C").Replace("3", "D").Replace("4", "E")
            .Replace("5", "F").Replace("6", "G").Replace("7", "H").Replace("8", "I").Replace("9", "J");
        for (var m = 5; m >= 1; m--)
        {
            await SpendAsync(http, acct, 15.49m, merchant, 30 * m);
        }

        await SpendAsync(http, acct, 17.99m, merchant, 1);

        await NavigateAsync("/");
        await WaitForLoadAsync();
        await Page!.GetByText("Needs a Look").First.WaitForAsync(new() { Timeout = 30000 });
        var flagged = Page.GetByText("Price increase", new() { Exact = false });
        await flagged.First.WaitForAsync(new() { Timeout = 20000 });
        (await flagged.First.InnerTextAsync()).Should().Contain("$17.99").And.Contain("$15.49");
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "Forecasts: shows the 'With everyday spending' range once there is spending history")]
    public async Task Forecasts_ShowsEverydaySpendingBand()
    {
        if (ShouldSkip) { return; }

        using var http = Api();
        var acct = await CreateAccountAsync(http, Unique("E2E Fc"));
        for (var week = 1; week <= 12; week++)
        {
            await SpendAsync(http, acct, 80m + week % 4 * 25m, Unique("Corner Grocer"), week * 7 - 1);
        }

        await NavigateAsync("/forecasts");
        await WaitForLoadAsync();
        var card = Page!.GetByText("With everyday spending");
        await card.First.WaitForAsync(new() { Timeout = 30000 });
        var body = await Page.Locator("body").InnerTextAsync();
        body.Should().Contain("a week on everything else").And.Contain("80% likely between");
        AssertNoPageErrors();
    }

    [Fact(DisplayName = "Query: 'Ask in plain English' writes and runs a query using the local model")]
    public async Task Query_AskInPlainEnglish_WritesAndRunsAQuery()
    {
        if (ShouldSkip) { return; }

        using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
        {
            try { (await probe.GetAsync("http://localhost:11434/api/tags")).IsSuccessStatusCode.Should().BeTrue(); }
            catch (Exception) { return; } // no local model available: nothing to validate
        }

        using var http = Api();
        var acct = await CreateAccountAsync(http, Unique("E2E Ask"));
        await SpendAsync(http, acct, 12m, Unique("Ask Cafe"), 1);

        await NavigateAsync("/query");
        await WaitForLoadAsync();
        var box = Page!.GetByPlaceholder("e.g. How much did I spend on dining last month?");
        await box.FillAsync("How many transactions do I have?");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Ask" }).ClickAsync();

        // The cold model load can take a couple of minutes; the editor is filled with the generated query once it answers.
        var editor = Page.Locator("textarea.fl-editor-input");
        await Assertions.Expect(editor).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("transactions", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            new() { Timeout = 240000 });
        await Page.Locator("table").First.WaitForAsync(new() { Timeout = 30000 });
        (await Page.Locator("body").InnerTextAsync()).Should().Contain("Written by your local AI model");
        AssertNoPageErrors();
    }
}
