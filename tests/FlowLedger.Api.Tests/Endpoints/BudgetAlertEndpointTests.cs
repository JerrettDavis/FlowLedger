using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>Pins the wire contract of /api/budgets, /api/goals and /api/alerts.</summary>
[Collection("ApiIntegration")]
public sealed class BudgetAlertEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Put_budget_with_negative_limit_returns_validation_problem_keyed_budget()
    {
        var response = await _client.PutAsJsonAsync("/api/budgets", new { category = "Food", monthlyLimit = -1m });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("errors").GetProperty("budget")[0].GetString()
            .Should().Be("A category and a non-negative limit are required.");
    }

    [Fact]
    public async Task Put_then_list_budgets_returns_month_budgets_and_spend()
    {
        (await _client.PutAsJsonAsync("/api/budgets", new { category = " Food ", monthlyLimit = 250m }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/api/budgets"));
        var root = doc.RootElement;
        root.GetProperty("month").GetString().Should().Be(factory.ServerNow.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        root.GetProperty("spendByCategory").ValueKind.Should().Be(JsonValueKind.Object);
        var budget = root.GetProperty("budgets").EnumerateArray().Single(b => b.GetProperty("category").GetString() == "Food");
        budget.GetProperty("monthlyLimit").GetDecimal().Should().Be(250m);
        budget.GetProperty("spent").GetDecimal().Should().Be(0m);
        budget.GetProperty("remaining").GetDecimal().Should().Be(250m);
        budget.GetProperty("percentUsed").GetDouble().Should().Be(0d);
        budget.GetProperty("over").GetBoolean().Should().BeFalse();

        (await _client.DeleteAsync("/api/budgets?category=Food")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_rollover_budget_carries_last_months_unspent_amount_and_alerts_use_the_larger_limit()
    {
        var accountResponse = await _client.PostAsJsonAsync(
            "/api/accounts", FlowLedger.TestSupport.Builders.CreateAccountRequestBuilder.A().Named("Rollover Account").OfType("Checking").StartingAt(0m).Build());
        accountResponse.EnsureSuccessStatusCode();
        var accountId = (await accountResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var categoryResponse = await _client.PostAsJsonAsync(
            "/api/categories", FlowLedger.TestSupport.Builders.CreateCategoryRequestBuilder.A().AtPath("RolloverFood").Named("Rollover Food").Build());
        categoryResponse.EnsureSuccessStatusCode();
        var categoryId = (await categoryResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        async Task AddSpendAsync(decimal amount, DateOnly date)
        {
            var created = await _client.PostAsJsonAsync("/api/transactions",
                new FlowLedger.Contracts.CreateTransactionRequest(accountId, amount, "USD", "Debit", "Rollover Cafe " + date, date, null, null, null, null));
            created.EnsureSuccessStatusCode();
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            (await _client.PutAsJsonAsync($"/api/transactions/{id}/category", new { categoryId, applyToMerchant = false })).EnsureSuccessStatusCode();
        }

        var thisMonth = new DateOnly(factory.ServerToday.Year, factory.ServerToday.Month, 1);
        await AddSpendAsync(40m, thisMonth.AddMonths(-1).AddDays(3)); // last month: 40 of the 100 limit spent, 60 left over
        await AddSpendAsync(130m, thisMonth);                          // this month: 130, over the base limit but under 100 + 60

        (await _client.PutAsJsonAsync("/api/budgets", new { category = "RolloverFood", monthlyLimit = 100m, rollover = true })).EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/api/budgets"));
        var budget = doc.RootElement.GetProperty("budgets").EnumerateArray().Single(b => b.GetProperty("category").GetString() == "RolloverFood");
        budget.GetProperty("rollover").GetBoolean().Should().BeTrue();
        budget.GetProperty("monthlyLimit").GetDecimal().Should().Be(100m, "the limit the user set is unchanged");
        budget.GetProperty("carried").GetDecimal().Should().Be(60m);
        budget.GetProperty("remaining").GetDecimal().Should().Be(30m, "160 effective limit minus 130 spent");
        budget.GetProperty("over").GetBoolean().Should().BeFalse();

        // The carried amount is stored: reading again (or editing with the version the read returned) still works.
        var version = budget.GetProperty("version").GetUInt32();
        (await Send(HttpMethod.Put, "/api/budgets", new { category = "RolloverFood", monthlyLimit = 100m }, $"\"{version}\"")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        using var alerts = JsonDocument.Parse(await _client.GetStringAsync("/api/alerts"));
        alerts.RootElement.EnumerateArray().Select(a => a.GetProperty("title").GetString())
            .Should().NotContain(t => t != null && t.Contains("RolloverFood", StringComparison.Ordinal) && t.Contains("over budget", StringComparison.Ordinal),
                "130 is over the 100 limit but not over the 160 the rollover allows");

        // Turning rollover off drops the carry and the budget is judged against the base limit again.
        (await _client.PutAsJsonAsync("/api/budgets", new { category = "RolloverFood", monthlyLimit = 100m, rollover = false })).EnsureSuccessStatusCode();
        using var off = JsonDocument.Parse(await _client.GetStringAsync("/api/budgets"));
        var plain = off.RootElement.GetProperty("budgets").EnumerateArray().Single(b => b.GetProperty("category").GetString() == "RolloverFood");
        plain.GetProperty("carried").GetDecimal().Should().Be(0m);
        plain.GetProperty("over").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Put_goal_with_zero_target_returns_validation_problem_keyed_goal()
    {
        var response = await _client.PutAsJsonAsync("/api/goals", new { name = "Car", targetAmount = 0m, savedAmount = 0m });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("errors").GetProperty("goal")[0].GetString()
            .Should().Be("A name, a positive target and a non-negative saved amount are required.");
    }

    [Fact]
    public async Task Alerts_flag_overdue_goal()
    {
        (await _client.PutAsJsonAsync("/api/goals", new { name = "Late", targetAmount = 100m, savedAmount = 0m, targetDate = "2000-01-01" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/api/alerts"));
        doc.RootElement.EnumerateArray()
            .Should().Contain(a => a.GetProperty("title").GetString() == "Goal \"Late\" is past its target date"
                && a.GetProperty("severity").GetString() == "warning");
    }

    // ── Account-tied goals (#70) ──────────────────────────────────────────────

    private async Task<Guid> CreateAccountAsync(string name, decimal startingBalance)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/accounts", FlowLedger.TestSupport.Builders.CreateAccountRequestBuilder.A().Named(name).OfType("Savings").StartingAt(startingBalance).Build());
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<decimal> BalanceOfAsync(Guid accountId)
    {
        using var accounts = JsonDocument.Parse(await _client.GetStringAsync("/api/accounts"));
        return accounts.RootElement.EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == accountId).GetProperty("balanceAmount").GetDecimal();
    }

    [Fact]
    public async Task A_goal_linked_to_an_account_is_measured_on_that_accounts_balance()
    {
        var account = await CreateAccountAsync("Rainy Day", 250m);
        (await _client.PutAsJsonAsync("/api/goals", new { name = "Fund", targetAmount = 1000m, savedAmount = 10m, linkedAccountId = account })).EnsureSuccessStatusCode();
        (await _client.PutAsJsonAsync("/api/goals", new { name = "Manual", targetAmount = 1000m, savedAmount = 100m })).EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/api/goals"));
        var goals = doc.RootElement.EnumerateArray().ToList();
        var linked = goals.Single(g => g.GetProperty("name").GetString() == "Fund");
        var balance = await BalanceOfAsync(account);
        linked.GetProperty("linkedAccountId").GetGuid().Should().Be(account);
        linked.GetProperty("linkedAccountName").GetString().Should().Be("Rainy Day");
        linked.GetProperty("currentAmount").GetDecimal().Should().Be(balance);
        linked.GetProperty("savedAmount").GetDecimal().Should().Be(10m, "the hand-entered amount is kept, but progress follows the account");
        linked.GetProperty("remaining").GetDecimal().Should().Be(1000m - balance);
        linked.GetProperty("percentComplete").GetDouble().Should().BeApproximately((double)(balance / 10m), 0.01);

        var manual = goals.Single(g => g.GetProperty("name").GetString() == "Manual");
        manual.GetProperty("currentAmount").GetDecimal().Should().Be(100m);
        manual.GetProperty("linkedAccountId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_goal_cannot_be_linked_to_an_account_that_does_not_exist()
    {
        var response = await _client.PutAsJsonAsync("/api/goals",
            new { name = "Ghost", targetAmount = 1000m, savedAmount = 0m, linkedAccountId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("errors").GetProperty("linkedAccountId")[0].GetString().Should().Contain("linked account was not found");
    }

    [Fact]
    public async Task A_goal_cannot_be_linked_to_another_tenants_account()
    {
        var other = factory.CreateAuthenticatedClient(Guid.NewGuid());
        var foreign = await other.PostAsJsonAsync(
            "/api/accounts", FlowLedger.TestSupport.Builders.CreateAccountRequestBuilder.A().Named("Foreign").OfType("Savings").StartingAt(10m).Build());
        foreign.EnsureSuccessStatusCode();
        var foreignId = (await foreign.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var response = await _client.PutAsJsonAsync("/api/goals", new { name = "Steal", targetAmount = 1000m, savedAmount = 0m, linkedAccountId = foreignId });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "another tenant's account looks like a missing one, never a server error");
        (await _client.GetStringAsync("/api/goals")).Should().NotContain("Steal");
    }

    [Fact]
    public async Task A_goal_can_be_unlinked_again_and_goes_back_to_its_saved_amount()
    {
        var account = await CreateAccountAsync("Temp Link", 400m);
        var saved = await _client.PutAsJsonAsync("/api/goals", new { name = "Flip", targetAmount = 1000m, savedAmount = 50m, linkedAccountId = account });
        var id = (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        (await _client.PutAsJsonAsync("/api/goals", new { id, name = "Flip", targetAmount = 1000m, savedAmount = 50m })).EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/api/goals"));
        var goal = doc.RootElement.EnumerateArray().Single(g => g.GetProperty("name").GetString() == "Flip");
        goal.GetProperty("linkedAccountId").ValueKind.Should().Be(JsonValueKind.Null);
        goal.GetProperty("currentAmount").GetDecimal().Should().Be(50m);
    }

    [Fact]
    public async Task Alerts_expose_goal_slipping_for_a_linked_account_trend_and_a_low_balance_naming_the_goal()
    {
        var account = await CreateAccountAsync("Goal Savings", 0m);

        async Task AddCreditAsync(decimal amount, int daysAgo)
        {
            var date = factory.ServerToday.AddDays(-daysAgo);
            (await _client.PostAsJsonAsync("/api/transactions",
                new FlowLedger.Contracts.CreateTransactionRequest(account, amount, "USD", "Credit", "Deposit " + daysAgo, date, null, null, null, null))).EnsureSuccessStatusCode();
        }

        // 90 days of history: the account gained a little each month, so its own trend is small and far from the goal.
        await AddCreditAsync(20m, 85);
        await AddCreditAsync(20m, 55);
        await AddCreditAsync(20m, 25);
        (await _client.PutAsJsonAsync("/api/goals", new
        {
            name = "Boat Fund",
            targetAmount = 50_000m,
            savedAmount = 0m,
            targetDate = factory.ServerToday.AddMonths(6).ToString("yyyy-MM-dd"),
            linkedAccountId = account,
        })).EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/api/alerts"));
        var alerts = doc.RootElement.EnumerateArray().ToList();

        var slip = alerts.Single(a => a.TryGetProperty("kind", out var k) && k.GetString() == "goal_slipping");
        slip.GetProperty("title").GetString().Should().Be("Goal behind schedule");
        slip.GetProperty("detail").GetString().Should().Contain("Boat Fund").And.Contain("Goal Savings's 90-day trend");
        var low = alerts.Single(a => a.TryGetProperty("kind", out var k) && k.GetString() == "low_balance");
        low.GetProperty("detail").GetString().Should().Contain("Goal Savings").And.Contain("goal \"Boat Fund\"");
    }

    // ── Optimistic concurrency (#157) ─────────────────────────────────────────

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body, string? ifMatch)
    {
        var message = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            message.Content = JsonContent.Create(body);
        }

        if (ifMatch is not null)
        {
            message.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return _client.SendAsync(message);
    }

    private async Task<uint> BudgetVersion(string category)
    {
        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/api/budgets"));
        return doc.RootElement.GetProperty("budgets").EnumerateArray()
            .Single(b => b.GetProperty("category").GetString() == category).GetProperty("version").GetUInt32();
    }

    [Fact]
    public async Task Budget_put_with_current_if_match_succeeds_and_returns_a_new_etag()
    {
        (await _client.PutAsJsonAsync("/api/budgets", new { category = "Fuel", monthlyLimit = 100m })).EnsureSuccessStatusCode();
        var version = await BudgetVersion("Fuel");

        var response = await Send(HttpMethod.Put, "/api/budgets", new { category = "Fuel", monthlyLimit = 120m }, $"\"{version}\"");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var next = await BudgetVersion("Fuel");
        next.Should().NotBe(version);
        response.Headers.ETag!.Tag.Should().Be($"\"{next}\"");
    }

    [Fact]
    public async Task Budget_put_and_delete_with_a_stale_if_match_return_409_problem_and_change_nothing()
    {
        (await _client.PutAsJsonAsync("/api/budgets", new { category = "Rent", monthlyLimit = 1000m })).EnsureSuccessStatusCode();
        var stale = await BudgetVersion("Rent");
        (await Send(HttpMethod.Put, "/api/budgets", new { category = "Rent", monthlyLimit = 1100m }, $"\"{stale}\"")).EnsureSuccessStatusCode();

        var put = await Send(HttpMethod.Put, "/api/budgets", new { category = "Rent", monthlyLimit = 1 }, $"\"{stale}\"");
        var delete = await Send(HttpMethod.Delete, "/api/budgets?category=Rent", null, $"\"{stale}\"");

        put.StatusCode.Should().Be(HttpStatusCode.Conflict);
        put.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        delete.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/api/budgets"));
        doc.RootElement.GetProperty("budgets").EnumerateArray()
            .Single(b => b.GetProperty("category").GetString() == "Rent").GetProperty("monthlyLimit").GetDecimal().Should().Be(1100m);
    }

    [Fact]
    public async Task Budget_writes_without_if_match_stay_last_write_wins_and_a_malformed_one_is_400()
    {
        (await _client.PutAsJsonAsync("/api/budgets", new { category = "Misc", monthlyLimit = 10m })).EnsureSuccessStatusCode();

        (await _client.PutAsJsonAsync("/api/budgets", new { category = "Misc", monthlyLimit = 20m })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Send(HttpMethod.Put, "/api/budgets", new { category = "Misc", monthlyLimit = 30m }, "\"nope\"")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Delete, "/api/budgets?category=Misc", null, "\"nope\"")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Goal_put_and_delete_honour_if_match()
    {
        var created = await _client.PutAsJsonAsync("/api/goals", new { name = "Trip", targetAmount = 500m, savedAmount = 0m });
        created.EnsureSuccessStatusCode();
        using var createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdDoc.RootElement.GetProperty("id").GetGuid();
        var v1 = createdDoc.RootElement.GetProperty("version").GetUInt32();
        created.Headers.ETag!.Tag.Should().Be($"\"{v1}\"");

        var ok = await Send(HttpMethod.Put, "/api/goals", new { id, name = "Trip", targetAmount = 500m, savedAmount = 50m }, $"\"{v1}\"");
        ok.StatusCode.Should().Be(HttpStatusCode.OK);

        var stalePut = await Send(HttpMethod.Put, "/api/goals", new { id, name = "Trip", targetAmount = 500m, savedAmount = 99m }, $"\"{v1}\"");
        var staleDelete = await Send(HttpMethod.Delete, $"/api/goals/{id}", null, $"\"{v1}\"");

        stalePut.StatusCode.Should().Be(HttpStatusCode.Conflict);
        stalePut.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        staleDelete.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var list = JsonDocument.Parse(await _client.GetStringAsync("/api/goals"));
        var goal = list.RootElement.EnumerateArray().Single(g => g.GetProperty("id").GetGuid() == id);
        goal.GetProperty("savedAmount").GetDecimal().Should().Be(50m);

        var current = goal.GetProperty("version").GetUInt32();
        (await Send(HttpMethod.Delete, $"/api/goals/{id}", null, $"\"{current}\"")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
