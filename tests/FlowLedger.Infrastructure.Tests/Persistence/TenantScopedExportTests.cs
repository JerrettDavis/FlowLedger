using System.IO.Compression;
using System.Text.Json;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>#139: an export can be scoped to one tenant; the bundle holds only that tenant's rows and cannot be restored by mistake.</summary>
[Collection("Integration")]
public sealed class TenantScopedExportTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fl-tenant-exp-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private async Task SeedAsync(TestTenantContext tenant, string accountName, string merchant)
    {
        var accountId = await fixture.SeedAccountAsync(tenant, accountName);
        await using var ctx = fixture.CreateDbContext(tenant);
        var date = new DateOnly(2025, 6, 1);
        ctx.Transactions.Add(Transaction.RecordActual(
            TenantId.From(tenant.TenantId), accountId, new Money(5m, Currency.Usd), TransactionDirection.Debit, merchant,
            effectiveDate: date, postedDate: date, source: TransactionSource.Manual));
        await ctx.SaveChangesAsync();
    }

    [DockerFact]
    public async Task A_tenant_export_contains_only_that_tenants_rows_and_records_the_scope()
    {
        var a = TestTenantContext.New();
        var b = TestTenantContext.New();
        await SeedAsync(a, "Alpha account", "Alpha merchant");
        await SeedAsync(b, "Bravo account", "Bravo merchant");

        var zip = await SnapshotImport.ExportZipAsync(fixture.ConnectionString, _root, tenantId: a.TenantId);

        using var archive = ZipFile.OpenRead(zip);
        string Read(string name) => new StreamReader(archive.GetEntry(name)!.Open()).ReadToEnd();
        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(Read("manifest.json"))!;
        manifest.TenantId.Should().Be(a.TenantId);
        Read("accounts.csv").Should().Contain("Alpha account").And.NotContain("Bravo account");
        Read("transactions.csv").Should().Contain("Alpha merchant").And.NotContain("Bravo merchant");
        archive.Entries.Select(e => e.Name).Should().NotContain("__EFMigrationsHistory.csv");
        manifest.Tables.Single(t => t.Table == "accounts").Rows.Should().Be(1);

        // Every exported CSV that has a tenant_id column holds nobody else's rows.
        foreach (var entry in archive.Entries.Where(e => e.Name.EndsWith(".csv", StringComparison.Ordinal)))
        {
            var text = Read(entry.Name);
            text.Should().NotContain(b.TenantId.ToString(), $"{entry.Name} must not leak the other tenant");
        }
    }

    [DockerFact]
    public async Task A_tenant_scoped_bundle_is_refused_by_import_and_a_full_export_is_unchanged()
    {
        var a = TestTenantContext.New();
        await SeedAsync(a, "Alpha account", "Alpha merchant");
        var scoped = await SnapshotImport.ExportZipAsync(fixture.ConnectionString, _root, tenantId: a.TenantId);

        var target = await CreateEmptyDatabaseAsync("flowledger_scoped_import");
        await using var stream = File.OpenRead(scoped);
        var result = await SnapshotImport.ImportZipAsync(target, stream, _root);
        result.Ok.Should().BeFalse();
        result.Problems.Should().ContainMatch("*single-tenant*");

        var full = await SnapshotImport.ExportZipAsync(fixture.ConnectionString, _root);
        using var archive = ZipFile.OpenRead(full);
        JsonSerializer.Deserialize<SnapshotManifest>(new StreamReader(archive.GetEntry("manifest.json")!.Open()).ReadToEnd())!.TenantId.Should().BeNull();
    }

    private async Task<string> CreateEmptyDatabaseAsync(string name)
    {
        var admin = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres", Pooling = false };
        await using var conn = new NpgsqlConnection(admin.ConnectionString);
        await conn.OpenAsync();
        await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", conn))
        {
            await drop.ExecuteNonQueryAsync();
        }

        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn))
        {
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name, Pooling = false }.ConnectionString;
    }
}
