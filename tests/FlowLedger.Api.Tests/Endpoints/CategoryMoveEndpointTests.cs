using System.Net;
using System.Net.Http.Json;
using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Budgets;
using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Features.Transactions;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>Real-Postgres checks of the category move/merge SQL: references, rules and budgets must all follow, atomically.</summary>
[Collection("ApiIntegration")]
public sealed class CategoryMoveEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<CategoryDto> Cat(string path)
        => (await (await _client.PostAsJsonAsync("/api/categories", new CreateCategoryRequest(path, path.Split('/').Last(), null)))
            .Content.ReadFromJsonAsync<CategoryDto>())!;

    private async Task<TransactionDto> Spend(Guid account, string desc, Guid categoryId)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var resp = await _client.PostAsJsonAsync("/api/transactions",
            new CreateTransactionRequest(account, 10m, "USD", "Debit", desc, today, today, categoryId, desc, null));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TransactionDto>())!;
    }

    private async Task<Guid?> CategoryOf(Guid transactionId)
        => (await _client.GetFromJsonAsync<TransactionDto>($"/api/transactions/{transactionId}"))!.CategoryId;

    private async Task<List<CategoryDto>> Categories()
        => (await _client.GetFromJsonAsync<List<CategoryDto>>("/api/categories"))!;

    [Fact]
    public async Task Rename_moves_the_subtree_and_rewrites_rules_and_budgets()
    {
        var food = await Cat("Food");
        var dining = await Cat("Food/Dining");
        var acct = (await (await _client.PostAsJsonAsync("/api/accounts",
            new CreateAccountRequest("Checking", "Checking", 0m, "USD", null, null))).Content.ReadFromJsonAsync<AccountDto>())!;
        var tx = await Spend(acct.Id, "Cafe", dining.Id);
        (await _client.PutAsJsonAsync("/api/budgets", new { category = "Food/Dining", monthlyLimit = 100m })).EnsureSuccessStatusCode();
        (await _client.PutAsJsonAsync("/api/category-rules", new { pattern = "cafe", categoryPath = "Food/Dining" })).EnsureSuccessStatusCode();

        var resp = await _client.PostAsJsonAsync($"/api/categories/{food.Id}/move", new { newPath = "Meals" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var paths = (await Categories()).Select(c => c.Path).ToList();
        paths.Should().Contain(["Meals", "Meals/Dining"]).And.NotContain(["Food", "Food/Dining"]);
        (await CategoryOf(tx.Id)).Should().Be(dining.Id); // identity kept, only the path changed
        (await _client.GetFromJsonAsync<List<CategoryRuleDto>>("/api/category-rules"))!
            .Should().ContainSingle(r => r.Pattern.ToUpper() == "CAFE" && r.CategoryPath == "Meals/Dining");
        (await _client.GetFromJsonAsync<BudgetOverview>("/api/budgets"))!.Budgets
            .Should().ContainSingle(b => b.Category == "Meals/Dining" && b.MonthlyLimit == 100m);
    }

    [Fact]
    public async Task Merge_repoints_transactions_deletes_the_source_and_adds_budgets()
    {
        var food = await Cat("Food");
        var eating = await Cat("Eating Out");
        var acct = (await (await _client.PostAsJsonAsync("/api/accounts",
            new CreateAccountRequest("Checking", "Checking", 0m, "USD", null, null))).Content.ReadFromJsonAsync<AccountDto>())!;
        var a = await Spend(acct.Id, "Cafe", food.Id);
        var b = await Spend(acct.Id, "Diner", eating.Id);
        (await _client.PutAsJsonAsync("/api/budgets", new { category = "Food", monthlyLimit = 100m })).EnsureSuccessStatusCode();
        (await _client.PutAsJsonAsync("/api/budgets", new { category = "Eating Out", monthlyLimit = 50m })).EnsureSuccessStatusCode();

        var preview = await _client.PostAsJsonAsync($"/api/categories/{food.Id}/move", new { newPath = "Eating Out", dryRun = true });
        (await preview.Content.ReadFromJsonAsync<CategoryRestructureResult>())!.Merged.Should().Be(1);
        (await Categories()).Select(c => c.Path).Should().Contain("Food"); // preview changed nothing

        var resp = await _client.PostAsJsonAsync($"/api/categories/{food.Id}/move", new { newPath = "Eating Out" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await resp.Content.ReadFromJsonAsync<CategoryRestructureResult>())!;
        result.Merged.Should().Be(1);
        result.TransactionsAffected.Should().Be(1);
        (await Categories()).Select(c => c.Path).Should().NotContain("Food");
        (await CategoryOf(a.Id)).Should().Be(eating.Id);
        (await CategoryOf(b.Id)).Should().Be(eating.Id);
        (await _client.GetFromJsonAsync<BudgetOverview>("/api/budgets"))!.Budgets
            .Should().ContainSingle(x => x.Category == "Eating Out" && x.MonthlyLimit == 150m);
    }

    [Fact]
    public async Task Invalid_move_is_rejected_with_400_and_unknown_category_with_404()
    {
        var food = await Cat("Food");

        (await _client.PostAsJsonAsync($"/api/categories/{food.Id}/move", new { newPath = "Food/Inside" })).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await _client.PostAsJsonAsync($"/api/categories/{Guid.NewGuid()}/move", new { newPath = "X" })).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }
}
