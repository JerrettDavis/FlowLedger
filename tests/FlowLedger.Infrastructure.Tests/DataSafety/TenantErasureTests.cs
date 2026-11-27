using FlowLedger.Application.Features.HomeAssistant;
using FlowLedger.Application.Features.Users;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.DataSafety;

/// <summary>#169 / #189: tenants registry, orphan handling and per-tenant erasure across every tenant-scoped table.</summary>
[Collection("Integration")]
public sealed class TenantErasureTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Household = new("00000000-0000-0000-0000-000000000001");
    private static readonly string Key = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 3)).ToArray());

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private EfUserDirectory Directory() => new(new HostDb(fixture.ConnectionString), new LegacyTenantClaim(Household, null));

    private EfTenantErasure Eraser(IEnumerable<ITenantFileEraser>? files = null)
        => new(fixture.CreateProductionStyleContext(null), Household, files ?? []);

    [Fact]
    public void Every_tenant_scoped_entity_type_is_in_the_catalog_and_no_root_entity_is_unscoped()
    {
        using var ctx = fixture.CreateProductionStyleContext(null);
        var catalog = TenantDataCatalog.FromModel(ctx.Model);

        var scoped = ctx.Model.GetEntityTypes()
            .Where(e => !e.IsOwned() && e.BaseType is null && e.FindProperty("TenantId") is not null)
            .Select(e => e.GetTableName()!)
            .Distinct()
            .ToList();
        catalog.Select(c => c.Table).Should().BeEquivalentTo(scoped);

        var unscoped = ctx.Model.GetEntityTypes()
            .Where(e => !e.IsOwned() && e.BaseType is null && e.FindProperty("TenantId") is null
                && !FlowLedger.Infrastructure.Persistence.HostWideEntities.Tables.ContainsKey(e.GetTableName() ?? string.Empty))
            .Select(e => e.ClrType.Name)
            .ToList();
        unscoped.Should().BeEmpty("a root entity without a tenant column would survive erasure unless it is listed in HostWideEntities");

        catalog.Select(c => c.Table).Should().Contain("outbox_messages");
        catalog.Should().OnlyContain(c => c.TenantColumn == "tenant_id");
    }

    [Fact]
    public void Ordering_puts_children_before_their_parents_and_survives_a_cycle()
    {
        TenantDataCatalog.OrderChildrenFirst(["accounts", "transactions", "splits"], [("transactions", "accounts"), ("splits", "transactions")])
            .Should().Equal("splits", "transactions", "accounts");
        TenantDataCatalog.OrderChildrenFirst(["a", "b"], [("a", "b"), ("b", "a")]).Should().BeEquivalentTo(["a", "b"]);
    }

    [Theory]
    [InlineData("accounts; DROP TABLE x")]
    [InlineData("Accounts")]
    [InlineData("a b")]
    public void Unsafe_identifiers_are_rejected(string name)
        => ((Action)(() => TenantDataCatalog.Validate(name))).Should().Throw<InvalidOperationException>();

    [DockerFact]
    public async Task Erasing_a_tenant_leaves_zero_rows_for_it_in_every_tenant_table_and_keeps_the_other_tenant()
    {
        var a = TestTenantContext.New();
        var b = TestTenantContext.New();
        var dir = Directory();

        // Seed both tenants: aggregates (and an outbox row), HA settings, an ad-hoc login and a link code.
        foreach (var t in new[] { a, b })
        {
            var accountId = await fixture.SeedAccountAsync(t, "acct");
            await using (var txCtx = fixture.CreateDbContext(t))
            {
                var date = new DateOnly(2025, 6, 1);
                txCtx.Transactions.Add(Transaction.RecordActual(
                    TenantId.From(t.TenantId), accountId, new Money(5m, Currency.Usd), TransactionDirection.Debit, "Shop",
                    effectiveDate: date, postedDate: date, source: TransactionSource.Manual));
                new FlowLedger.Infrastructure.Events.EfOutbox(txCtx, t).Enqueue(
                    new FlowLedger.Domain.Events.SyncCompleted(TenantId.From(t.TenantId), 1, 1, 0));
                await txCtx.SaveChangesAsync();
            }

            var ctx = fixture.CreateProductionStyleContext(t);
            await new EfHomeAssistantStore(ctx, t, Key).SaveAsync(t.TenantId, true, "https://ha.example.com/api/webhook/AbCdEf0123456789XyZ", false, false, true, 3, HaEventKinds.All);
        }

        await SeedLoginAsync(a.TenantId, "sub-a");
        await SeedLoginAsync(b.TenantId, "sub-b");
        await dir.CreateLinkCodeAsync(a.TenantId);
        await dir.CreateLinkCodeAsync(b.TenantId);

        var result = await Eraser().EraseAsync(a.TenantId);
        result.TotalRows.Should().BeGreaterThan(0);

        var tables = await TablesWithTenantColumnAsync();
        tables.Should().Contain(["accounts", "transactions", "outbox_messages", "app_users", "app_user_link_codes", "home_assistant_settings"]);
        foreach (var table in tables)
        {
            (await CountAsync(table, a.TenantId)).Should().Be(0, $"{table} must hold nothing for the erased tenant");
        }

        foreach (var table in new[] { "accounts", "transactions", "outbox_messages", "app_users", "app_user_link_codes", "home_assistant_settings" })
        {
            (await CountAsync(table, b.TenantId)).Should().BeGreaterThan(0, $"{table} of the other tenant must be intact");
        }

        // Audit record: the registry row survives, marked deleted; erasing again is a harmless no-op.
        var info = await new EfTenantRegistry(new HostDb(fixture.ConnectionString)).GetAsync(a.TenantId);
        info!.Status.Should().Be(TenantStatus.Deleted);
        info.DeletedUtc.Should().NotBeNull();
        (await Eraser().EraseAsync(a.TenantId)).TotalRows.Should().Be(0);
    }

    [DockerFact]
    public async Task The_household_tenant_and_an_empty_id_are_refused()
    {
        var act = () => Eraser().EraseAsync(Household);
        await act.Should().ThrowAsync<TenantErasureRefusedException>();
        var empty = () => Eraser().EraseAsync(Guid.Empty);
        await empty.Should().ThrowAsync<TenantErasureRefusedException>();
    }

    [DockerFact]
    public async Task File_erasers_run_and_their_count_is_reported()
    {
        var eraser = new CountingFileEraser();
        var result = await Eraser([eraser]).EraseAsync(Guid.NewGuid());
        result.FilesDeleted.Should().Be(3);
        eraser.Calls.Should().Be(1);
    }

    [DockerFact]
    public async Task Repointing_the_last_login_orphans_the_old_tenant_and_it_stays_visible()
    {
        var dir = Directory();
        var first = await dir.ResolveAsync(new ExternalIdentity("iss", "one", null, null));
        var second = await dir.ResolveAsync(new ExternalIdentity("iss", "two", null, null));
        var registry = new EfTenantRegistry(new HostDb(fixture.ConnectionString));

        (await registry.GetAsync(first.TenantId))!.Status.Should().Be(TenantStatus.Active);
        (await registry.GetAsync(second.TenantId))!.Status.Should().Be(TenantStatus.Active);

        var code = await dir.CreateLinkCodeAsync(second.TenantId);
        (await dir.RedeemLinkCodeAsync(first.UserId, code.Code)).Should().Be(LinkOutcome.Linked);

        var orphan = await registry.GetAsync(first.TenantId);
        orphan!.Status.Should().Be(TenantStatus.Orphaned);
        orphan.OrphanedUtc.Should().NotBeNull();
        (await registry.ListAsync(TenantStatus.Orphaned)).Select(t => t.Id).Should().Equal(first.TenantId);
        (await registry.GetAsync(second.TenantId))!.Status.Should().Be(TenantStatus.Active);

        // An orphan can be offboarded like any tenant; the live one is untouched.
        await Eraser().EraseAsync(first.TenantId);
        (await registry.GetAsync(first.TenantId))!.Status.Should().Be(TenantStatus.Deleted);
        (await dir.ListIdentitiesAsync(second.TenantId)).Should().HaveCount(2);
    }

    [DockerFact]
    public async Task Unlink_registers_the_new_tenant_and_existing_logins_are_backfilled()
    {
        var dir = Directory();
        var first = await dir.ResolveAsync(new ExternalIdentity("iss", "one", null, null));
        var second = await dir.ResolveAsync(new ExternalIdentity("iss", "two", null, null));
        var code = await dir.CreateLinkCodeAsync(first.TenantId);
        await dir.RedeemLinkCodeAsync(second.UserId, code.Code);
        (await dir.UnlinkAsync(first.TenantId, second.UserId)).Should().BeTrue();

        var registry = new EfTenantRegistry(new HostDb(fixture.ConnectionString));
        var active = (await registry.ListAsync(TenantStatus.Active)).Select(t => t.Id).ToList();
        var detachedTenant = (await dir.ListTenantIdsAsync()).Except([first.TenantId, Household]).Single();
        active.Should().Contain([first.TenantId, detachedTenant, Household]);
    }

    private async Task SeedLoginAsync(Guid tenantId, string subject)
    {
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        // Touch the directory once so the ad-hoc tables exist, then insert the login directly under the chosen tenant.
        await Directory().ListTenantIdsAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO app_users (id, issuer, subject, tenant_id) VALUES (gen_random_uuid(), 'iss', @s, @t)", conn);
        cmd.Parameters.AddWithValue("s", subject);
        cmd.Parameters.AddWithValue("t", tenantId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> TablesWithTenantColumnAsync()
    {
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.columns WHERE table_schema = 'public' AND column_name = 'tenant_id' ORDER BY 1", conn);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(r.GetString(0));
        }

        return list;
    }

    private async Task<long> CountAsync(string table, Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM \"{TenantDataCatalog.Validate(table)}\" WHERE tenant_id = @t", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private sealed class CountingFileEraser : ITenantFileEraser
    {
        public int Calls { get; private set; }

        public Task<int> EraseTenantAsync(Guid tenantId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(3);
        }
    }
}
