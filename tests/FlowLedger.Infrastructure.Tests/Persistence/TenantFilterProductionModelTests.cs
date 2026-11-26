using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>
/// #141: tenant query filters must not depend on which context built the EF model first.
/// These tests deliberately build contexts the way production does: the default internal
/// service provider (caching ON), no per-instance model tricks.
/// </summary>
[Collection("Integration")]
public sealed class TenantFilterProductionModelTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;

    public TenantFilterProductionModelTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [DockerFact]
    public async Task UnscopedContextBuiltFirst_DoesNotUnfilterLaterTenantContexts()
    {
        var a = TestTenantContext.New();
        var b = TestTenantContext.New();

        // Seed one account per tenant through an unscoped (admin) context.
        await using (var admin = _fixture.CreateProductionStyleContext(null))
        {
            admin.Accounts.Add(Account.Create(TenantId.From(a.TenantId), "A-acct", AccountType.Checking, new Money(1m, Currency.Usd)));
            admin.Accounts.Add(Account.Create(TenantId.From(b.TenantId), "B-acct", AccountType.Checking, new Money(2m, Currency.Usd)));
            await admin.SaveChangesAsync();
            // The unscoped context sees both.
            Assert.Equal(2, await admin.Accounts.CountAsync());
        }

        await using var asB = _fixture.CreateProductionStyleContext(b);
        var names = await asB.Accounts.Select(x => x.Name).ToListAsync();
        Assert.Equal(["B-acct"], names);
    }

    [DockerFact]
    public async Task TenantContextBuiltFirst_DoesNotFilterUnscopedContextIncorrectly()
    {
        var a = TestTenantContext.New();
        var b = TestTenantContext.New();

        await using (var asA = _fixture.CreateProductionStyleContext(a))
        {
            asA.Accounts.Add(Account.Create(TenantId.From(a.TenantId), "A-acct", AccountType.Checking, new Money(1m, Currency.Usd)));
            await asA.SaveChangesAsync();
            Assert.Single(await asA.Accounts.ToListAsync());
        }

        await using var asB = _fixture.CreateProductionStyleContext(b);
        Assert.Empty(await asB.Accounts.ToListAsync());

        await using var admin = _fixture.CreateProductionStyleContext(null);
        Assert.Single(await admin.Accounts.ToListAsync());
    }

    [DockerFact]
    public async Task EveryEntityTypeHasAQueryFilter_InTheModelOfAnyContext()
    {
        foreach (var tenant in new TestTenantContext?[] { null, TestTenantContext.New() })
        {
            await using var ctx = _fixture.CreateProductionStyleContext(tenant);
            var unfiltered = ctx.Model.GetEntityTypes()
                .Where(e => !e.IsOwned() && e.BaseType is null && e.GetDeclaredQueryFilters().Count == 0)
                .Select(e => e.ClrType.Name)
                .ToList();
            Assert.True(unfiltered.Count == 0, $"Entity types without a tenant query filter: {string.Join(", ", unfiltered)}");
        }
    }

    [DockerFact]
    public async Task WriteGuard_rejects_cross_tenant_writes_for_every_tenant_owned_type_not_just_aggregates()
    {
        // #172: the guard used to cover 5 aggregate types only; ad-hoc records (reports, snapshots, connections...) were unchecked.
        var a = TestTenantContext.New();
        var otherTenant = Guid.NewGuid();

        await using var db = _fixture.CreateProductionStyleContext(a);
        db.SavedReports.Add(FlowLedger.Infrastructure.Persistence.Entities.SavedReportRecord.Create(
            otherTenant, "sneaky", null, "View", "[]", TestClock.Instant));

        var act = () => db.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Cross-tenant*");
    }

    [Fact]
    public void ResolvingTheDbContext_WithoutATenantContextRegistration_Fails()
    {
        var services = new ServiceCollection();
        services.AddScoped<IDomainEventDispatcher, NoOpDomainEventDispatcher>();
        services.AddDbContext<FlowLedgerDbContext>(o => o.UseNpgsql("Host=localhost;Database=x"));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<FlowLedgerDbContext>());
    }

    [DockerFact]
    public async Task Synchronous_SaveChanges_is_not_supported_so_event_dispatch_never_blocks()
    {
        await using var db = _fixture.CreateProductionStyleContext(TestTenantContext.New());
        var act = () => db.SaveChanges();
        act.Should().Throw<NotSupportedException>();
    }

    [DockerFact]
    public async Task A_failed_save_keeps_the_staged_outbox_event_so_a_retry_still_records_it()
    {
        // #92: an event must never be lost because the save that should carry it failed.
        var t = TestTenantContext.New();
        await using var db = _fixture.CreateProductionStyleContext(t);
        db.Accounts.Add(Account.Create(TenantId.From(t.TenantId), "Evt", AccountType.Checking, new Money(1m, Currency.Usd)));
        new FlowLedger.Infrastructure.Events.EfOutbox(db, t).Enqueue(
            new FlowLedger.Domain.Events.SyncCompleted(TenantId.From(t.TenantId), 1, 1, 0));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => db.SaveChangesAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        await db.SaveChangesAsync();

        await using var check = _fixture.CreateProductionStyleContext(t);
        (await check.OutboxMessages.CountAsync()).Should().Be(1);
    }
}
