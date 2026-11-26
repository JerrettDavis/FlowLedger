using System.Data.Common;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.SharedKernel;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>
/// The post-sync steps (aliases, rules, categorization, enrichment) read the history without tracking it and write back only the
/// rows they changed, in bounded batches (#163): one history SELECT and a tracker that never holds the whole history.
/// </summary>
[Collection("Integration")]
public sealed class PostSyncPipelineLoadTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    public PostSyncPipelineLoadTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class CommandLog : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Sql.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private async Task<(TestTenantContext Tenant, int Count)> SeedAsync(int count)
    {
        var tenant = TestTenantContext.New();
        var account = await _fixture.SeedAccountAsync(tenant);
        await using var seed = _fixture.CreateDbContext(tenant);
        var tenantId = TenantId.From(tenant.TenantId);
        for (var i = 0; i < count; i++)
        {
            seed.Transactions.Add(Transaction.RecordActual(
                tenantId, account, new Money(5m + i, Currency.Usd), TransactionDirection.Debit,
                $"COFFEE SHOP {i}", new DateOnly(2026, 1, 1).AddDays(i % 300), new DateOnly(2026, 1, 1).AddDays(i % 300), TransactionSource.Manual));
        }

        await seed.SaveChangesAsync();
        return (tenant, count);
    }

    [DockerFact]
    public async Task Alias_apply_loads_history_once_untracked_and_writes_only_changed_rows()
    {
        var (tenant, count) = await SeedAsync(1200);
        var log = new CommandLog();
        var options = new DbContextOptionsBuilder<FlowLedgerDbContext>()
            .UseNpgsql(_fixture.ConnectionString, n => n.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name))
            .AddInterceptors(log)
            .Options;
        await using var db = new FlowLedgerDbContext(options, new NoOpDomainEventDispatcher(), tenant);
        var aliases = new EfMerchantAliasStore(db, tenant);
        await aliases.SetAsync("COFFEE SHOP", "Cafe");
        var service = new MerchantAliasService(aliases, new CategorizationStore(db));
        log.Sql.Clear();

        var changed = await service.ApplyAsync();

        changed.Should().Be(count);
        log.Sql.Count(sql => sql.Contains("FROM transactions", StringComparison.OrdinalIgnoreCase)).Should().Be(1, "one history read, no per-row lookups");
        db.ChangeTracker.Entries<Transaction>().Should().BeEmpty("saved batches are detached, the history is never tracked");
        await using var verify = _fixture.CreateDbContext(tenant);
        (await verify.Transactions.CountAsync(t => t.MerchantName == "Cafe")).Should().Be(count);
    }

    [DockerFact]
    public async Task Alias_apply_with_nothing_to_change_writes_nothing()
    {
        var (tenant, _) = await SeedAsync(50);
        await using var db = _fixture.CreateDbContext(tenant);
        var aliases = new EfMerchantAliasStore(db, tenant);
        await aliases.SetAsync("NO SUCH TEXT", "Nothing");

        var changed = await new MerchantAliasService(aliases, new CategorizationStore(db)).ApplyAsync();

        changed.Should().Be(0);
        db.ChangeTracker.Entries<Transaction>().Should().BeEmpty();
    }
}
