using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Transactions;

public sealed class MerchantAliasServiceTests
{
    private sealed class AliasStore : IMerchantAliasStore
    {
        public Dictionary<string, string> Rules { get; } = new();
        public Task<IReadOnlyList<MerchantAliasDto>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MerchantAliasDto>>(Rules.Select(r => new MerchantAliasDto(r.Key, r.Value)).ToList());
        public Task SetAsync(string pattern, string alias, CancellationToken ct = default) { Rules[pattern] = alias; return Task.CompletedTask; }
        public Task<bool> RemoveAsync(string pattern, CancellationToken ct = default) => Task.FromResult(Rules.Remove(pattern));
    }

    private sealed class TxStore(List<Transaction> txs) : ICategorizationStore
    {
        public int Saves { get; private set; }
        public Task<IReadOnlyList<Transaction>> LoadTrackedAsync(int max, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Transaction>>(txs);
        public Task SaveAsync(CancellationToken ct = default) { Saves++; return Task.CompletedTask; }
    }

    private static Transaction Tx(string description) => Transaction.RecordActual(
        TenantId.New(), AccountId.New(), new Money(20m, Currency.Usd), TransactionDirection.Debit, description,
        new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 5), TransactionSource.Manual, FlowLedger.TestSupport.TestClock.Provider);

    [Fact]
    public async Task Alias_renames_matching_transactions_case_and_spacing_insensitively_and_keeps_description()
    {
        var a = Tx("NANONOBLE PTE. LTD. SINGAPORE SGP");
        var b = Tx("nanonoble   pte. ltd.  singapore sgp");
        var other = Tx("Bluejay Grocery");
        var txStore = new TxStore([a, b, other]);
        var svc = new MerchantAliasService(new AliasStore(), txStore);

        var changed = await svc.SetAsync("NANONOBLE", "MiniMax");

        changed.Should().Be(2);
        a.MerchantName.Should().Be("MiniMax");
        b.MerchantName.Should().Be("MiniMax");
        a.Description.Should().Be("NANONOBLE PTE. LTD. SINGAPORE SGP");
        other.MerchantName.Should().BeNull();
        txStore.Saves.Should().Be(1);
    }

    [Fact]
    public async Task Apply_is_idempotent_and_picks_up_new_transactions()
    {
        var txs = new List<Transaction> { Tx("NANONOBLE PTE. LTD.") };
        var svc = new MerchantAliasService(new AliasStore(), new TxStore(txs));
        await svc.SetAsync("nanonoble", "MiniMax");
        (await svc.ApplyAsync()).Should().Be(0);

        txs.Add(Tx("NANONOBLE PTE. LTD. SINGAPORE"));
        (await svc.ApplyAsync()).Should().Be(1);
        txs[1].MerchantName.Should().Be("MiniMax");
    }

    [Fact]
    public async Task Longest_pattern_wins()
    {
        var t = Tx("NANONOBLE PTE. LTD. SINGAPORE SGP");
        var svc = new MerchantAliasService(new AliasStore(), new TxStore([t]));
        await svc.SetAsync("NANO", "Short");
        await svc.SetAsync("NANONOBLE PTE", "Long");
        t.MerchantName.Should().Be("Long");
    }

    [Theory]
    [InlineData("", "x")]
    [InlineData("x", "  ")]
    public async Task Blank_pattern_or_alias_is_rejected(string pattern, string alias)
    {
        var svc = new MerchantAliasService(new AliasStore(), new TxStore([]));
        await svc.Invoking(s => s.SetAsync(pattern, alias)).Should().ThrowAsync<ArgumentException>();
    }
}
