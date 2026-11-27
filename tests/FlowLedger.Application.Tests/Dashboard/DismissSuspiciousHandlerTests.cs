using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Features.Dashboard;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Dashboard;

public sealed class DismissSuspiciousHandlerTests
{
    private sealed class Dismissals : ISuspiciousDismissalStore
    {
        public List<(Guid Id, string? Note)> Calls { get; } = [];
        public Task<IReadOnlySet<Guid>> ListIdsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlySet<Guid>>(Calls.Select(c => c.Id).ToHashSet());
        public Task DismissAsync(Guid transactionId, string? note, CancellationToken ct = default) { Calls.Add((transactionId, note)); return Task.CompletedTask; }
    }

    private sealed class TxStore(List<Transaction> txs) : ICategorizationStore
    {
        public int Saves { get; private set; }
        public Task<IReadOnlyList<Transaction>> LoadTrackedAsync(int max, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Transaction>>(txs);
        public Task SaveAsync(CancellationToken ct = default) { Saves++; return Task.CompletedTask; }
    }

    private static Transaction Tx() => Transaction.RecordActual(
        TenantId.New(), AccountId.New(), new Money(20m, Currency.Usd), TransactionDirection.Debit, "ACME",
        new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 5), TransactionSource.Manual, FlowLedger.TestSupport.TestClock.Provider);

    [Fact]
    public async Task Dismiss_with_a_note_also_records_it_on_the_transaction()
    {
        var tx = Tx();
        var dismissals = new Dismissals();
        var store = new TxStore([tx]);

        await new DismissSuspiciousHandler(dismissals, store).HandleAsync(tx.Id, "  verified with merchant ");

        dismissals.Calls.Should().ContainSingle().Which.Note.Should().Be("verified with merchant");
        tx.Notes.Should().Be("verified with merchant");
        store.Saves.Should().Be(1);
    }

    [Fact]
    public async Task Dismiss_without_a_note_leaves_the_transaction_alone()
    {
        var tx = Tx();
        var store = new TxStore([tx]);

        await new DismissSuspiciousHandler(new Dismissals(), store).HandleAsync(tx.Id, "  ");

        tx.Notes.Should().BeNull();
        store.Saves.Should().Be(0);
    }
}
