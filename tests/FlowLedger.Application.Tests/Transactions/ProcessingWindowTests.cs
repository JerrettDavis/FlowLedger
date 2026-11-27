using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Features.Duplicates;
using FlowLedger.Application.Features.LocalAi;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Application.Tests.Fakes;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Transactions;

/// <summary>#166: whole-history engines say so when more rows are stored than they process, instead of silently dropping the oldest.</summary>
public sealed class ProcessingWindowTests
{
    private static readonly TenantId Tenant = TenantId.From(FakeTenantContext.DefaultTenantId);

    /// <summary>Newest-first rows with distinct amounts so no duplicate pairs form (keeps the detector linear).</summary>
    private static List<Transaction> History(Account account, int count)
    {
        var start = new DateOnly(2000, 1, 1);
        return Enumerable.Range(0, count)
            .Select(i => Transaction.RecordActual(
                Tenant, account.AccountId, new Money(1m + i, Currency.Usd), TransactionDirection.Debit, "Zzyzx " + i,
                start.AddDays(i % 20_000), start.AddDays(i % 20_000), TransactionSource.Manual, FlowLedger.TestSupport.TestClock.Provider))
            .OrderByDescending(t => t.EffectiveDate)
            .ToList();
    }

    private sealed class CapAwareCategorizationStore(List<Transaction> txs) : ICategorizationStore
    {
        public Task<IReadOnlyList<Transaction>> LoadTrackedAsync(int max, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Transaction>>(txs.Take(max).ToList());

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapAwareDuplicateStore(List<Transaction> txs) : IDuplicateStore
    {
        public Task<IReadOnlyList<Transaction>> LoadTrackedAsync(int max, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Transaction>>(txs.Take(max).ToList());

        public Task<IReadOnlySet<string>> DismissedKeysAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        public Task DismissAsync(string key, CancellationToken ct = default) => Task.CompletedTask;

        public Task MergeAsync(Transaction survivor, Transaction duplicate, bool auto, string reason, string snapshotJson, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<MergeLogEntry>> RecentMergesAsync(int take, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MergeLogEntry>>([]);
    }

    private static Account Checking() => Account.Create(Tenant, "Checking", AccountType.Checking, new Money(0m, Currency.Usd), FlowLedger.TestSupport.TestClock.Provider);

    [Fact]
    public void IsPartial_flips_exactly_at_the_cap()
    {
        TransactionQuota.IsPartial(TransactionQuota.ProcessingCap - 1).Should().BeFalse();
        TransactionQuota.IsPartial(TransactionQuota.ProcessingCap).Should().BeTrue();
    }

    [Fact]
    public async Task Categorization_over_sixty_thousand_rows_reports_partial_and_a_small_history_does_not()
    {
        var account = Checking();
        var accounts = new FakeAccountRepository();
        await accounts.AddAsync(account);

        var big = new CategorizationService(new CapAwareCategorizationStore(History(account, 60_000)), new FakeCategoryRepository(), accounts, new FakeTenantContext(), new CategorizationOptions(), new LocalAiOptions());
        var small = new CategorizationService(new CapAwareCategorizationStore(History(account, 50)), new FakeCategoryRepository(), accounts, new FakeTenantContext(), new CategorizationOptions(), new LocalAiOptions());

        (await big.RunAsync()).Partial.Should().BeTrue("the oldest 10,000 rows are outside the processing window");
        (await small.RunAsync()).Partial.Should().BeFalse();
    }

    [Fact]
    public async Task Duplicate_scan_over_the_cap_reports_partial()
    {
        var account = Checking();

        var big = await new DuplicateService(new CapAwareDuplicateStore(History(account, 60_000)), new DuplicateOptions()).ScanAsync();
        var small = await new DuplicateService(new CapAwareDuplicateStore(History(account, 50)), new DuplicateOptions()).ScanAsync();

        big.Partial.Should().BeTrue();
        small.Partial.Should().BeFalse();
    }

    [Fact]
    public async Task Processing_window_endpoint_handler_reports_stored_versus_cap()
    {
        var repo = new FakeTransactionRepository { ReportedCount = 60_000 };

        var dto = await new GetProcessingWindowHandler(repo).HandleAsync();

        dto.Partial.Should().BeTrue();
        dto.StoredTransactions.Should().Be(60_000);
        dto.ProcessingCap.Should().Be(TransactionQuota.ProcessingCap);

        repo.ReportedCount = 10;
        (await new GetProcessingWindowHandler(repo).HandleAsync()).Partial.Should().BeFalse();
    }
}
