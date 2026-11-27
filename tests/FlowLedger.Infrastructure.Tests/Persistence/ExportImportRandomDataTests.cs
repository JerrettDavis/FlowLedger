using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.TestSupport;
using FluentAssertions;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>
/// INV-11: export then import is lossless, not just count-preserving. Seeded random tenants, accounts and transactions (names built from
/// characters that break naive CSV handling) are exported and imported into an empty database; every row must come back byte for byte,
/// compared by a content hash of each table, not only by row count.
/// </summary>
[Collection("Integration")]
[Trait("Invariant", "INV-11")]
public sealed class ExportImportRandomDataTests : IAsyncLifetime
{
    private static readonly string[] Alphabet =
        ["a", "B", "7", " ", ",", "\"", "'", "\n", "\r\n", "\t", ";", "|", "é", "ß", "漢", "😀", "\\", "NULL", "=", "-"];

    private readonly IntegrationTestFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fl-inv11-" + Guid.NewGuid().ToString("N"));

    public ExportImportRandomDataTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static string RandomText(Random rng, int maxPieces)
    {
        var count = rng.Next(1, maxPieces + 1);
        return string.Concat(Enumerable.Range(0, count).Select(_ => Alphabet[rng.Next(Alphabet.Length)]));
    }

    private async Task SeedAsync(int seed)
    {
        var rng = new Random(seed);
        var date = new DateOnly(2025, 1, 1);
        foreach (var _ in Enumerable.Range(0, rng.Next(2, 4)))
        {
            var tenant = TestTenantContext.New();
            await using var ctx = _fixture.CreateDbContext(tenant);
            var tenantId = TenantId.From(tenant.TenantId);
            var accounts = new List<Account>();
            foreach (var __ in Enumerable.Range(0, rng.Next(1, 4)))
            {
                var account = Account.Create(
                    tenantId, "A" + RandomText(rng, 12), (AccountType)rng.Next(0, 3), new Money(rng.Next(-100000, 100000) / 100m, Currency.Usd), TestClock.Provider,
                    institution: rng.Next(2) == 0 ? null : RandomText(rng, 6));
                accounts.Add(account);
                ctx.Accounts.Add(account);
            }

            await ctx.SaveChangesAsync();

            foreach (var account in accounts)
            {
                foreach (var ___ in Enumerable.Range(0, rng.Next(0, 6)))
                {
                    ctx.Transactions.Add(Transaction.RecordActual(
                        tenantId, account.AccountId, new Money(rng.Next(1, 500000) / 100m, Currency.Usd),
                        rng.Next(2) == 0 ? TransactionDirection.Debit : TransactionDirection.Credit, "T" + RandomText(rng, 20),
                        effectiveDate: date.AddDays(rng.Next(0, 400)), postedDate: date.AddDays(rng.Next(0, 400)), source: TransactionSource.Manual,
                        time: TestClock.Provider));
                }
            }

            await ctx.SaveChangesAsync();
        }
    }

    private static async Task<string> CreateEmptyDatabaseAsync(IntegrationTestFixture fixture, string name)
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

    private static async Task<(long Rows, string Hash)> FingerprintAsync(string connectionString, string table)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT count(*), md5(coalesce(string_agg(t::text, E'\\n' ORDER BY t::text), '')) FROM {table} t", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetString(1));
    }

    [DockerTheory]
    [InlineData(11)]
    [InlineData(23)]
    [InlineData(42)]
    [InlineData(1337)]
    public async Task Random_data_survives_export_and_import_byte_for_byte(int seed)
    {
        await SeedAsync(seed);
        var zip = await SnapshotImport.ExportZipAsync(_fixture.ConnectionString, _root);
        var target = await CreateEmptyDatabaseAsync(_fixture, "flowledger_inv11_" + seed);

        await using var stream = File.OpenRead(zip);
        var result = await SnapshotImport.ImportZipAsync(target, stream, _root);

        result.Ok.Should().BeTrue(string.Join("; ", result.Problems));
        foreach (var table in new[] { "accounts", "transactions" })
        {
            var before = await FingerprintAsync(_fixture.ConnectionString, table);
            var after = await FingerprintAsync(target, table);
            before.Rows.Should().BeGreaterThan(0, $"the seed must produce {table}");
            after.Should().Be(before, $"{table} must come back identical, not merely the same size (seed {seed})");
        }
    }
}
