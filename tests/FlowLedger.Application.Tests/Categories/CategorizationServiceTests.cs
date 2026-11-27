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
        _checking = Account.Create(tenantId, "Checking", AccountType.Checking, new Money(100m, Currency.Usd), FlowLedger.TestSupport.TestClock.Provider);
        _savings = Account.Create(tenantId, "Savings", AccountType.Savings, new Money(100m, Currency.Usd), FlowLedger.TestSupport.TestClock.Provider);
        await _accounts.AddAsync(_checking);
        await _accounts.AddAsync(_savings);
        return new CategorizationService(new Store(_txs), _categories, _accounts, _tenant);
    }

    private Transaction Add(Account account, decimal amount, TransactionDirection dir, string desc, int day = 5)
    {
        var tx = Transaction.RecordActual(
            TenantId.From(_tenant.TenantId), account.AccountId, new Money(amount, Currency.Usd), dir, desc,
            new DateOnly(2026, 11, day), new DateOnly(2026, 11, day), TransactionSource.Manual, FlowLedger.TestSupport.TestClock.Provider);
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

    private async Task<(CategorizationService svc, Domain.ValueObjects.CategoryId dining, Domain.ValueObjects.CategoryId pay)> SetUpTrainedAsync(CategorizationOptions? options = null)
    {
        var tenantId = TenantId.From(_tenant.TenantId);
        _checking = Account.Create(tenantId, "Checking", AccountType.Checking, new Money(100m, Currency.Usd), FlowLedger.TestSupport.TestClock.Provider);
        await _accounts.AddAsync(_checking);
        var dining = Category.Create(tenantId, new CategoryPath("Food/Dining"), "Dining");
        var pay = Category.Create(tenantId, new CategoryPath("Income/Paycheck"), "Paycheck");
        await _categories.AddAsync(dining);
        await _categories.AddAsync(pay);
        for (var i = 0; i < 15; i++)
        {
            Add(_checking, 20, TransactionDirection.Debit, $"Zorba Eats {i}").Categorize(dining.CategoryId, "Zorba Eats " + i);
            Add(_checking, 2000, TransactionDirection.Credit, $"Quark Industries {i}").Categorize(pay.CategoryId, "Quark Industries " + i);
        }

        return (new CategorizationService(new Store(_txs), _categories, _accounts, _tenant, options), dining.CategoryId, pay.CategoryId);
    }

    [Fact]
    public async Task Model_applies_confident_prediction_as_auto_with_confidence()
    {
        var (svc, dining, _) = await SetUpTrainedAsync(new CategorizationOptions { MlThreshold = 0.7 });
        var fresh = Add(_checking, 22, TransactionDirection.Debit, "Zorba Eats 77");

        var result = await svc.RunAsync();

        result.ByModel.Should().Be(1);
        fresh.CategoryId.Should().Be(dining);
        fresh.CategorySource.Should().Be(CategorySource.Auto);
        fresh.CategoryConfidence.Should().BeGreaterThan(0.8);
    }

    [Fact]
    public async Task Model_stores_midconfidence_as_suggestion_only()
    {
        var (svc, dining, _) = await SetUpTrainedAsync(new CategorizationOptions { SuggestionFloor = 0.35 });
        var fresh = Add(_checking, 22, TransactionDirection.Debit, "Zorba Eats 77");

        var result = await svc.RunAsync();

        result.Suggested.Should().Be(1);
        fresh.CategoryId.Should().BeNull();
        fresh.SuggestedCategoryId.Should().Be(dining);
        fresh.SuggestedConfidence.Should().BeGreaterThan(0.35);
    }

    [Fact]
    public async Task Model_never_touches_manual_categories()
    {
        var (svc, dining, pay) = await SetUpTrainedAsync(new CategorizationOptions { MlThreshold = 0.7 });
        var manual = Add(_checking, 22, TransactionDirection.Debit, "Zorba Eats 77");
        manual.Categorize(pay);

        await svc.RunAsync();

        manual.CategoryId.Should().Be(pay);
        manual.CategorySource.Should().Be(CategorySource.Manual);
    }

    [Fact]
    public async Task Manual_recategorize_clears_suggestion_and_confidence()
    {
        var (svc, dining, pay) = await SetUpTrainedAsync(new CategorizationOptions());
        var fresh = Add(_checking, 22, TransactionDirection.Debit, "Zorba Eats 77");
        await svc.RunAsync();
        fresh.SuggestedCategoryId.Should().NotBeNull();

        fresh.Categorize(pay);

        fresh.SuggestedCategoryId.Should().BeNull();
        fresh.SuggestedConfidence.Should().BeNull();
        fresh.CategoryConfidence.Should().BeNull();
    }

    [Fact]
    public async Task Provider_category_is_a_hint_for_rows_no_keyword_or_learned_merchant_covers()
    {
        var svc = await SetUpAsync();
        var hinted = Add(_checking, 33, TransactionDirection.Debit, "Zzyzx Holdings");
        hinted.SetProviderCategory("Gas & Fuel");
        var unhinted = Add(_checking, 9, TransactionDirection.Debit, "Qwerty Holdings");
        var vague = Add(_checking, 12, TransactionDirection.Debit, "Plugh Holdings");
        vague.SetProviderCategory("Uncategorized");

        var result = await svc.RunAsync();

        result.ByProviderHint.Should().Be(1);
        hinted.CategoryId.Should().NotBeNull();
        hinted.CategorySource.Should().Be(CategorySource.Auto);
        _categories.All.Should().ContainSingle(c => c.Path.Value == "Transport/Fuel");
        unhinted.CategoryId.Should().BeNull();
        vague.CategoryId.Should().BeNull("a vague provider label is no hint");
    }

    [Fact]
    public async Task A_learned_merchant_or_keyword_wins_over_the_provider_hint()
    {
        var svc = await SetUpAsync();
        var groceries = Add(_checking, 40, TransactionDirection.Debit, "Bluejay Grocery Co.");
        groceries.SetProviderCategory("Gas & Fuel");

        var result = await svc.RunAsync();

        result.ByKeyword.Should().Be(1);
        result.ByProviderHint.Should().Be(0);
        _categories.All.Should().ContainSingle(c => c.Path.Value == "Food/Groceries");
    }

    [Fact]
    public async Task Both_legs_of_a_transfer_get_the_same_persisted_link_and_rerunning_changes_nothing()
    {
        var svc = await SetUpAsync();
        var outLeg = Add(_checking, 500, TransactionDirection.Debit, "To savings", day: 3);
        var inLeg = Add(_savings, 500, TransactionDirection.Credit, "From checking", day: 3);
        var coffee = Add(_checking, 4.5m, TransactionDirection.Debit, "Zzyzx Coffee", day: 3);

        var first = await svc.RunAsync();

        first.TransfersLinked.Should().Be(2);
        outLeg.TransferLinkId.Should().NotBeNull().And.Be(inLeg.TransferLinkId);
        outLeg.TransferLinkId.Should().Be(FlowLedger.Application.Features.Transactions.TransferDetector.LinkIdFor(inLeg.Id, outLeg.Id));
        coffee.TransferLinkId.Should().BeNull();

        var second = await svc.RunForAsync([outLeg.Id, inLeg.Id]);
        second.TransfersLinked.Should().Be(0);
    }

    [Fact]
    public async Task Bulk_edit_sets_one_manual_category_on_every_selected_row_and_skips_unknown_ids()
    {
        var svc = await SetUpAsync();
        var a = Add(_checking, 10, TransactionDirection.Debit, "Zzyzx One");
        var b = Add(_checking, 20, TransactionDirection.Debit, "Qwerty Two");
        var untouched = Add(_checking, 30, TransactionDirection.Debit, "Plugh Three");
        var mine = Category.Create(TenantId.From(_tenant.TenantId), new CategoryPath("Mine/Special"), "Special");
        await _categories.AddAsync(mine);

        var changed = await svc.SetCategoryBulkAsync([a.Id, b.Id, Guid.NewGuid()], mine.Id);

        changed.Should().Be(2);
        a.CategoryId.Should().Be(mine.CategoryId);
        a.CategorySource.Should().Be(CategorySource.Manual);
        b.CategoryId.Should().Be(mine.CategoryId);
        untouched.CategoryId.Should().BeNull();
        (await svc.SetCategoryBulkAsync([a.Id, b.Id], mine.Id)).Should().Be(0, "already set by hand");
    }

    [Fact]
    public async Task Bulk_edit_rejects_an_unknown_category_and_an_oversized_selection()
    {
        var svc = await SetUpAsync();
        var a = Add(_checking, 10, TransactionDirection.Debit, "Zzyzx One");

        var unknown = () => svc.SetCategoryBulkAsync([a.Id], Guid.NewGuid());
        await unknown.Should().ThrowAsync<FlowLedger.Application.Abstractions.NotFoundException>();

        var tooMany = () => svc.SetCategoryBulkAsync(Enumerable.Range(0, CategorizationService.MaxBulkRows + 1).Select(_ => Guid.NewGuid()).ToList(), Guid.NewGuid());
        await tooMany.Should().ThrowAsync<FlowLedger.Application.Abstractions.RequestValidationException>();
    }
}
