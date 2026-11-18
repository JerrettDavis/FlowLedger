using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Tests.Fakes;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features.Accounts;

public sealed class ChangeAccountTypeHandlerTests
{
    private sealed class Store(List<Transaction> txs) : ICategorizationStore
    {
        public Task<IReadOnlyList<Transaction>> LoadTrackedAsync(int max, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Transaction>>(txs);

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private readonly FakeTenantContext _tenant = new();
    private readonly FakeAccountRepository _accounts = new();
    private readonly FakeCategoryRepository _categories = new();
    private readonly List<Transaction> _txs = [];

    private TenantId TenantId => TenantId.From(_tenant.TenantId);

    private ChangeAccountTypeHandler Handler() =>
        new(_accounts, new CategorizationService(new Store(_txs), _categories, _accounts, _tenant));

    private async Task<Account> AddAccountAsync(string name, AccountType type, decimal balance)
    {
        var account = Account.Create(TenantId, name, type, new Money(balance, Currency.Usd));
        await _accounts.AddAsync(account);
        return account;
    }

    private Transaction AddDebit(Account account, decimal amount, string description, Category? autoCategory = null, int day = 5)
    {
        var tx = Transaction.RecordActual(
            TenantId, account.AccountId, new Money(amount, Currency.Usd), TransactionDirection.Debit, description,
            new DateOnly(2026, 11, day), new DateOnly(2026, 11, day), TransactionSource.Manual);
        if (autoCategory is not null)
        {
            tx.Categorize(autoCategory.CategoryId, null, CategorySource.Auto);
        }

        _txs.Add(tx);
        return tx;
    }

    [Fact]
    public async Task Unknown_account_returns_null()
    {
        var result = await Handler().HandleAsync(Guid.NewGuid(), new ChangeAccountTypeRequest("CreditCard"));

        result.Should().BeNull();
    }

    [Fact]
    public async Task Changes_type_pins_it_and_flips_the_negative_balance()
    {
        var card = await AddAccountAsync("J. DAVIS", AccountType.Checking, -45.60m);

        var dto = await Handler().HandleAsync(card.Id, new ChangeAccountTypeRequest("creditcard"));

        dto!.AccountType.Should().Be("CreditCard");
        dto.TypeLockedByUser.Should().BeTrue();
        dto.BalanceAmount.Should().Be(45.60m);
    }

    [Fact]
    public async Task Card_payments_become_transfers_once_the_account_is_a_card()
    {
        var checking = await AddAccountAsync("Checking", AccountType.Checking, 5_000m);
        var card = await AddAccountAsync("J. DAVIS", AccountType.Checking, 300m);
        var other = Category.Create(TenantId, new CategoryPath("Bills/Other"), "Other");
        await _categories.AddAsync(other);

        // Both legs are debits with the same description: only a liability on one side makes them a transfer.
        var fromChecking = AddDebit(checking, 300m, "CHASE CARD PAYMENT", other);
        var onCard = AddDebit(card, 300m, "CHASE CARD PAYMENT", other);
        var manual = AddDebit(checking, 12m, "Corner Store");
        var mine = Category.Create(TenantId, new CategoryPath("Mine/Special"), "Special");
        await _categories.AddAsync(mine);
        manual.Categorize(mine.CategoryId);

        await Handler().HandleAsync(card.Id, new ChangeAccountTypeRequest("CreditCard"));

        var transfer = _categories.All.Single(c => c.Path.Value == "Transfer");
        fromChecking.CategoryId.Should().Be(transfer.CategoryId);
        onCard.CategoryId.Should().Be(transfer.CategoryId);
        manual.CategoryId.Should().Be(mine.CategoryId, "categories chosen by hand are never touched");
    }

    [Fact]
    public async Task Auto_transfers_are_released_when_the_account_stops_being_a_card()
    {
        var checking = await AddAccountAsync("Checking", AccountType.Checking, 5_000m);
        var card = await AddAccountAsync("Card", AccountType.CreditCard, 300m);
        var handler = Handler();
        var a = AddDebit(checking, 300m, "CHASE CARD PAYMENT");
        var b = AddDebit(card, 300m, "CHASE CARD PAYMENT");
        await new CategorizationService(new Store(_txs), _categories, _accounts, _tenant).RunAsync();
        var transfer = _categories.All.Single(c => c.Path.Value == "Transfer");
        a.CategoryId.Should().Be(transfer.CategoryId);

        await handler.HandleAsync(card.Id, new ChangeAccountTypeRequest("Savings"));

        a.CategoryId.Should().NotBe(transfer.CategoryId);
        b.CategoryId.Should().NotBe(transfer.CategoryId);
    }

    [Fact]
    public async Task Rejects_an_asset_type_for_a_negative_balance()
    {
        var card = await AddAccountAsync("Card", AccountType.CreditCard, -10m);

        var act = () => Handler().HandleAsync(card.Id, new ChangeAccountTypeRequest("Checking"));

        await act.Should().ThrowAsync<FlowLedger.Domain.Exceptions.InvalidBalanceException>();
    }
}
