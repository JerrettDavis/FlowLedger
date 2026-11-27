using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Imports;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Application.Tests.Fakes;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.TestSupport;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features;

/// <summary>#127: handlers that create aggregates hand them the injected clock, so stamped times are controllable in tests.</summary>
public sealed class InjectedClockHandlerTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly TimeProvider Clock = new FixedClock(TestClock.Instant);

    [Fact]
    public async Task CreateAccountHandler_stamps_the_account_with_the_injected_clock()
    {
        var repo = new FakeAccountRepository();
        var handler = new CreateAccountHandler(repo, new FakeTenantContext(), Clock);

        var dto = await handler.HandleAsync(new CreateAccountRequest("Checking", "Checking", 10m, "USD", null, null));

        (await repo.GetByIdAsync(AccountId.From(dto.Id)))!.CreatedAt.Should().Be(TestClock.Instant);
    }

    [Fact]
    public async Task CreateTransactionHandler_stamps_the_transaction_with_the_injected_clock()
    {
        var repo = new FakeTransactionRepository();
        var handler = new CreateTransactionHandler(repo, new FakeTenantContext(), new FakeReferenceValidator(), Clock);

        await handler.HandleAsync(new CreateTransactionRequest(
            Guid.NewGuid(), 5m, "USD", "Debit", "Coffee", new DateOnly(2026, 1, 30), null, null, null, null));

        repo.All.Single().CreatedAt.Should().Be(TestClock.Instant);
    }

    [Fact]
    public async Task ImportHandler_stamps_imported_transactions_with_the_injected_clock()
    {
        var txRepo = new FakeTransactionRepository();
        var accounts = new FakeAccountRepository();
        var occurrences = new FakePlannedOccurrenceRepository();
        var account = Account.Create(TenantId.From(FakeTenantContext.DefaultTenantId), "Checking", AccountType.Checking, new Money(100m, "USD"), TestClock.Provider);
        await accounts.AddAsync(account);
        var handler = new ImportTransactionsHandler(txRepo, accounts, occurrences, new FakeTenantContext(), new MatchingEngine(occurrences), Clock);

        await handler.HandleAsync(new ImportTransactionsCommand(
            account.Id, "Date,Amount,Description\n2026-01-10,-4.50,Cafe\n", new CsvColumnMapping(0, 1, 2)));

        txRepo.All.Single().CreatedAt.Should().Be(TestClock.Instant);
    }
}
