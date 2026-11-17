using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Tests.Fakes;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Categories;

public sealed class CategorizationServiceTests
{
    private sealed class Store(List<Transaction> txs) : ICategorizationStore
    {
        public int Saves { get; private set; }

        public Task<IReadOnlyList<Transaction>> LoadTrackedAsync(int max, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Transaction>>(txs);

        public Task SaveAsync(CancellationToken ct = default)
        {
            Saves++;
            return Task.CompletedTask;
        }
    }

    private readonly FakeTenantContext _tenant = new();
    private readonly FakeCategoryRepository _categories = new();
    private readonly FakeAccountRepository _accounts = new();
    private readonly List<Transaction> _txs = [];
    private Account _checking = null!;
    private Account _savings = null!;

    private async Task<CategorizationService> SetUpAsync()
    {
        var tenantId = TenantId.From(_tenant.TenantId);
        _checking = Account.Create(tenantId, "Checking", AccountType.Checking, new Money(100m, Currency.Usd));
        _savings = Account.Create(tenantId, "Savings", AccountType.Savings, new Money(100m, Currency.Usd));
        await _accounts.AddAsync(_checking);
        await _accounts.AddAsync(_savings);
        return new CategorizationService(new Store(_txs), _categories, _accounts, _tenant);
    }

    private Transaction Add(Account account, decimal amount, TransactionDirection dir, string desc, int day = 5)
    {
        var tx = Transaction.RecordActual(
            TenantId.From(_tenant.TenantId), account.AccountId, new Money(amount, Currency.Usd), dir, desc,
            new DateOnly(2026, 11, day), new DateOnly(2026, 11, day), TransactionSource.Manual);
        _txs.Add(tx);
        return tx;
    }

    [Fact]
    public async Task Keyword_rules_fill_missing_categories_and_create_the_categories()
    {
        var svc = await SetUpAsync();
        var groceries = Add(_checking, 40, TransactionDirection.Debit, "Bluejay Grocery Co.");
        var unknown = Add(_checking, 9, TransactionDirection.Debit, "Zzyzx Holdings");

        var result = await svc.RunAsync();

        result.Categorized.Should().Be(1);
        result.ByKeyword.Should().Be(1);
        result.StillUncategorized.Should().Be(1);
        groceries.CategoryId.Should().NotBeNull();
        unknown.CategoryId.Should().BeNull();
        _categories.All.Should().ContainSingle(c => c.Path.Value == "Food/Groceries");
    }

    [Fact]
    public async Task Existing_categories_are_never_overwritten()
    {
        var svc = await SetUpAsync();
        var manual = Add(_checking, 40, TransactionDirection.Debit, "Bluejay Grocery Co.");
        var mine = Category.Create(TenantId.From(_tenant.TenantId), new CategoryPath("Mine/Special"), "Special");
        await _categories.AddAsync(mine);
        manual.Categorize(mine.CategoryId);

        await svc.RunAsync();

        manual.CategoryId.Should().Be(mine.CategoryId);
    }

    [Fact]
    public async Task Transfers_are_tagged_and_learned_merchants_beat_keywords()
    {
        var svc = await SetUpAsync();
        var out1 = Add(_checking, 600, TransactionDirection.Debit, "Move money");
        var in1 = Add(_savings, 600, TransactionDirection.Credit, "Move money");

        // The user previously put "Bluejay Grocery Co." into "Household" (not what keywords would pick).
        var household = Category.Create(TenantId.From(_tenant.TenantId), new CategoryPath("Household"), "Household");
        await _categories.AddAsync(household);
        var earlier = Add(_checking, 20, TransactionDirection.Debit, "Bluejay Grocery Co.", day: 1);
        earlier.Categorize(household.CategoryId);
        var later = Add(_checking, 30, TransactionDirection.Debit, "Bluejay Grocery Co.", day: 2);

        var result = await svc.RunAsync();

        result.Transfers.Should().Be(2);
        result.ByLearnedMerchant.Should().Be(1);
        later.CategoryId.Should().Be(household.CategoryId);
        out1.CategoryId.Should().Be(in1.CategoryId).And.NotBeNull();
        _categories.All.Should().Contain(c => c.Path.Value == "Transfer");
    }

    [Fact]
    public async Task Running_twice_is_idempotent()
    {
        var svc = await SetUpAsync();
        Add(_checking, 40, TransactionDirection.Debit, "Bluejay Grocery Co.");

        await svc.RunAsync();
        var second = await svc.RunAsync();

        second.Categorized.Should().Be(0);
        _categories.All.Should().HaveCount(1);
    }

    [Fact]
    public async Task Setting_a_category_can_apply_to_the_same_uncategorized_merchant_only()
    {
        var svc = await SetUpAsync();
        var cat = Category.Create(TenantId.From(_tenant.TenantId), new CategoryPath("Pets"), "Pets");
        await _categories.AddAsync(cat);
        var first = Add(_checking, 10, TransactionDirection.Debit, "Paws & Claws", day: 1);
        var second = Add(_checking, 12, TransactionDirection.Debit, "Paws & Claws", day: 2);
        var keep = Add(_checking, 14, TransactionDirection.Debit, "Paws & Claws", day: 3);
        var other = Add(_checking, 16, TransactionDirection.Debit, "Somewhere Else", day: 4);
        var existing = Category.Create(TenantId.From(_tenant.TenantId), new CategoryPath("Vet"), "Vet");
        keep.Categorize(existing.CategoryId);

        var changed = await svc.SetCategoryAsync(first.Id, cat.CategoryId.Value, applyToMerchant: true);

        changed.Should().Be(2);
        first.CategoryId.Should().Be(cat.CategoryId);
        second.CategoryId.Should().Be(cat.CategoryId);
        keep.CategoryId.Should().Be(existing.CategoryId);
        other.CategoryId.Should().BeNull();
    }

    [Fact]
    public async Task A_manual_correction_replaces_auto_categories_for_the_merchant_but_not_other_manual_ones()
    {
        var svc = await SetUpAsync();
        var a = Add(_checking, 10, TransactionDirection.Debit, "Bluejay Grocery Co.", day: 1);
        var b = Add(_checking, 12, TransactionDirection.Debit, "Bluejay Grocery Co.", day: 2);
        var manualOther = Add(_checking, 14, TransactionDirection.Debit, "Bluejay Grocery Co.", day: 3);
        var household = Category.Create(TenantId.From(_tenant.TenantId), new CategoryPath("Household"), "Household");
        var keep = Category.Create(TenantId.From(_tenant.TenantId), new CategoryPath("Keep"), "Keep");
        await _categories.AddAsync(household);
        await _categories.AddAsync(keep);
        manualOther.Categorize(keep.CategoryId);

        await svc.RunAsync(); // auto-fills a and b as Food/Groceries
        a.CategorySource.Should().Be(CategorySource.Auto);

        await svc.SetCategoryAsync(a.Id, household.CategoryId.Value, applyToMerchant: true);

        a.CategoryId.Should().Be(household.CategoryId);
        a.CategorySource.Should().Be(CategorySource.Manual);
        b.CategoryId.Should().Be(household.CategoryId);
        b.CategorySource.Should().Be(CategorySource.Auto, "spread rows stay replaceable");
        manualOther.CategoryId.Should().Be(keep.CategoryId, "a category the user chose by hand is never overwritten");
    }

    [Fact]
    public async Task Learning_ignores_auto_rows_and_clear_auto_leaves_manual_rows()
    {
        var svc = await SetUpAsync();
        var auto = Add(_checking, 10, TransactionDirection.Debit, "Bluejay Grocery Co.", day: 1);
        var manual = Add(_checking, 12, TransactionDirection.Debit, "Zzyzx Holdings", day: 2);
        var mine = Category.Create(TenantId.From(_tenant.TenantId), new CategoryPath("Mine"), "Mine");
        await _categories.AddAsync(mine);
        manual.Categorize(mine.CategoryId);

        await svc.RunAsync();
        auto.CategoryId.Should().NotBeNull();

        var cleared = await svc.ClearAutoCategoriesAsync();

        cleared.Should().Be(1);
        auto.CategoryId.Should().BeNull();
        manual.CategoryId.Should().Be(mine.CategoryId);
    }
}
