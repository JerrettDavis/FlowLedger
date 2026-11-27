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
    public async Task Put_budget_with_negative_limit_returns_validation_problem_keyed_to_the_limit()
    {
        var response = await _client.PutAsJsonAsync("/api/budgets", new { category = "Food", monthlyLimit = -1m });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("errors").GetProperty("MonthlyLimit")[0].GetString()
            .Should().Be("The monthly limit must be between 0 and 100,000,000.");
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
    public async Task Put_goal_with_zero_target_returns_validation_problem_keyed_to_the_target()
    {
        var response = await _client.PutAsJsonAsync("/api/goals", new { name = "Car", targetAmount = 0m, savedAmount = 0m });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("errors").GetProperty("TargetAmount")[0].GetString()
            .Should().Be("The target must be greater than 0 and at most 1,000,000,000.");
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
