using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Tests.Fakes;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Categories;

public sealed class CategoryRuleServiceTests
{
    private sealed class RuleStore : ICategoryRuleStore
    {
        public Dictionary<string, string> Rules { get; } = new();
        public Task<IReadOnlyList<CategoryRuleDto>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CategoryRuleDto>>(Rules.Select(r => new CategoryRuleDto(r.Key, r.Value)).ToList());
        public Task SetAsync(string pattern, string categoryPath, CancellationToken ct = default) { Rules[pattern] = categoryPath; return Task.CompletedTask; }
        public Task<bool> RemoveAsync(string pattern, CancellationToken ct = default) => Task.FromResult(Rules.Remove(pattern));
    }

    private sealed class TxStore(List<Transaction> txs) : ICategorizationStore
    {
        public int Saves { get; private set; }
        public Task<IReadOnlyList<Transaction>> LoadTrackedAsync(int max, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Transaction>>(txs);
        public Task SaveAsync(CancellationToken ct = default) { Saves++; return Task.CompletedTask; }
    }

    private readonly FakeTenantContext _tenant = new();
    private readonly FakeCategoryRepository _categories = new();

    private Transaction Tx(string description) => Transaction.RecordActual(
        TenantId.From(_tenant.TenantId), AccountId.New(), new Money(20m, Currency.Usd), TransactionDirection.Debit, description,
        new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 5), TransactionSource.Manual);

    private CategoryRuleService Service(RuleStore rules, TxStore txs) => new(rules, txs, _categories, _tenant);

    [Fact]
    public void Longest_matching_pattern_wins()
    {
        var rules = new[] { new CategoryRuleDto("SHELL", "Auto/Gas"), new CategoryRuleDto("SHELL OIL CAFE", "Food/Coffee") };

        CategoryRuleService.Match(rules, "shell  oil cafe 123", null)!.CategoryPath.Should().Be("Food/Coffee");
        CategoryRuleService.Match(rules, "SHELL 4411", null)!.CategoryPath.Should().Be("Auto/Gas");
        CategoryRuleService.Match(rules, "Unrelated", null).Should().BeNull();
    }

    [Fact]
    public async Task Rule_categorizes_matching_transactions_and_creates_the_category()
    {
        var hit = Tx("ACME HARDWARE #12");
        var miss = Tx("Bluejay Grocery");
        var store = new TxStore([hit, miss]);

        var changed = await Service(new RuleStore(), store).SetAsync("acme hardware", "Home/Improvement");

        changed.Should().Be(1);
        hit.CategoryId.Should().NotBeNull();
        miss.CategoryId.Should().BeNull();
        _categories.All.Should().ContainSingle(c => c.Path.Value == "Home/Improvement");
        store.Saves.Should().Be(1);
    }

    [Fact]
    public async Task Rule_replaces_auto_category_but_never_a_manual_one()
    {
        var auto = Tx("ACME HARDWARE #1");
        var manual = Tx("ACME HARDWARE #2");
        var mine = Category.Create(TenantId.From(_tenant.TenantId), new CategoryPath("Mine/Special"), "Special");
        await _categories.AddAsync(mine);
        auto.Categorize(mine.CategoryId, null, CategorySource.Auto);
        manual.Categorize(mine.CategoryId);

        var changed = await Service(new RuleStore(), new TxStore([auto, manual])).SetAsync("ACME", "Home/Improvement");

        changed.Should().Be(1);
        auto.CategoryId.Should().NotBe(mine.CategoryId);
        manual.CategoryId.Should().Be(mine.CategoryId);
    }

    [Fact]
    public async Task Apply_is_idempotent()
    {
        var tx = Tx("ACME HARDWARE");
        var svc = Service(new RuleStore(), new TxStore([tx]));
        await svc.SetAsync("ACME", "Home/Improvement");

        (await svc.ApplyAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Blank_pattern_or_category_is_rejected()
    {
        var svc = Service(new RuleStore(), new TxStore([]));

        await FluentActions.Awaiting(() => svc.SetAsync("  ", "Food")).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => svc.SetAsync("ACME", " / ")).Should().ThrowAsync<ArgumentException>();
    }
}
