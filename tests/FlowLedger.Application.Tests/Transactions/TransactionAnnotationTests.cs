using FluentAssertions;
using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Features.Query;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Application.Tests.Fakes;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Tests.Transactions;

public sealed class TransactionAnnotationTests
{
    private sealed class TxStore(List<Transaction> txs) : ICategorizationStore
    {
        public int Saves { get; private set; }
        public Task<IReadOnlyList<Transaction>> LoadTrackedAsync(int max, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Transaction>>(txs);
        public Task SaveAsync(CancellationToken ct = default) { Saves++; return Task.CompletedTask; }
    }

    private sealed class NoAssets : IAssetRepository
    {
        public Task<Asset?> GetByIdAsync(AssetId id, CancellationToken ct = default) => Task.FromResult<Asset?>(null);
        public Task<IReadOnlyList<Asset>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Asset>>([]);
        public Task AddAsync(Asset asset, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoHistory : IBalanceHistoryReader
    {
        public Task<IReadOnlyList<BalancePoint>> DailyAccountBalancesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BalancePoint>>([]);
    }

    private readonly FakeTenantContext _tenant = new();
    private readonly FakeCategoryRepository _categories = new();
    private readonly FakeAccountRepository _accounts = new();
    private readonly FakeTransactionRepository _repo = new();
    private Transaction _tx = null!;
    private Category _groceries = null!;
    private Category _household = null!;
    private TxStore _store = null!;

    private async Task<TransactionAnnotationService> SetUpAsync()
    {
        var tenantId = TenantId.From(_tenant.TenantId);
        var account = Account.Create(tenantId, "Checking", AccountType.Checking, new Money(100m, Currency.Usd));
        await _accounts.AddAsync(account);
        _groceries = Category.Create(tenantId, new CategoryPath("Food/Groceries"), "Groceries");
        _household = Category.Create(tenantId, new CategoryPath("Home/Household"), "Household");
        await _categories.AddAsync(_groceries);
        await _categories.AddAsync(_household);
        _tx = Transaction.RecordActual(tenantId, account.AccountId, new Money(100m, Currency.Usd), TransactionDirection.Debit,
            "TARGET #123", new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 5), TransactionSource.Manual);
        await _repo.AddAsync(_tx);
        _store = new TxStore([_tx]);
        return new TransactionAnnotationService(_store, _categories);
    }

    [Fact]
    public async Task Note_can_be_set_and_cleared()
    {
        var svc = await SetUpAsync();

        await svc.SetNoteAsync(_tx.Id, "  reimbursable  ");
        _tx.Notes.Should().Be("reimbursable");

        await svc.SetNoteAsync(_tx.Id, "   ");
        _tx.Notes.Should().BeNull();
        _store.Saves.Should().Be(2);
    }

    [Fact]
    public async Task Overlong_note_and_unknown_transaction_are_rejected()
    {
        var svc = await SetUpAsync();

        await FluentActions.Awaiting(() => svc.SetNoteAsync(_tx.Id, new string('x', 1001))).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => svc.SetNoteAsync(Guid.NewGuid(), "x")).Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Splits_that_add_up_are_saved()
    {
        var svc = await SetUpAsync();

        await svc.SetSplitsAsync(_tx.Id, [new(70m, _groceries.Id, null), new(30m, _household.Id, "paper towels")]);

        _tx.Splits.Should().HaveCount(2);
        _tx.Splits[0].CategoryId.Should().Be(_groceries.CategoryId);
    }

    [Fact]
    public async Task Splits_that_do_not_add_up_single_part_or_foreign_category_are_rejected()
    {
        var svc = await SetUpAsync();

        await FluentActions.Awaiting(() => svc.SetSplitsAsync(_tx.Id, [new(60m, null, null), new(30m, null, null)])).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => svc.SetSplitsAsync(_tx.Id, [new(100m, null, null)])).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => svc.SetSplitsAsync(_tx.Id, [new(0m, null, null), new(100m, null, null)])).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => svc.SetSplitsAsync(_tx.Id, [new(50m, Guid.NewGuid(), null), new(50m, null, null)])).Should().ThrowAsync<KeyNotFoundException>();
        _tx.Splits.Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_list_removes_splits()
    {
        var svc = await SetUpAsync();
        await svc.SetSplitsAsync(_tx.Id, [new(70m, null, null), new(30m, null, null)]);

        await svc.SetSplitsAsync(_tx.Id, []);

        _tx.Splits.Should().BeEmpty();
    }

    [Fact]
    public async Task Query_dataset_emits_one_row_per_split_with_its_own_category_and_amount()
    {
        var svc = await SetUpAsync();
        await svc.SetSplitsAsync(_tx.Id, [new(70m, _groceries.Id, null), new(30m, _household.Id, null)]);
        var provider = new QueryDatasetProvider(_repo, _accounts, _categories, new NoAssets(), new NoHistory());

        var tables = await provider.BuildAsync();
        var result = QueryEngine.Execute(
            "transactions | summarize total = sum(amount) by category", tables, new DateOnly(2026, 11, 15));

        result.Rows.ToDictionary(r => (string)r[0]!, r => (decimal)r[1]!).Should()
            .BeEquivalentTo(new Dictionary<string, decimal> { ["Food/Groceries"] = 70m, ["Home/Household"] = 30m });
    }
}
