using System.Text.Json;
using FlowLedger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

public sealed record DrillTableResult(string Table, long ExpectedRows, long RestoredRows, bool Ok);

public sealed record RestoreDrillResult(bool Ok, string ScratchDatabase, bool ScratchKept, IReadOnlyList<DrillTableResult> Tables, IReadOnlyList<string> Problems);

/// <summary>
/// Proves a snapshot can actually be restored: it creates a scratch database, applies EF migrations up to the
/// snapshot's last applied migration, COPYs every table back in, and compares row counts with the manifest.
/// The live database is never touched and the scratch name must never equal it.
/// This same path is the disaster-recovery restore (use <c>keep: true</c> and promote the scratch database).
/// </summary>
public static class SnapshotRestoreDrill
{
    private static readonly System.Text.RegularExpressions.Regex SafeIdentifier = new("^[A-Za-z_][A-Za-z0-9_]{0,62}$");

    internal static readonly string[] SkippedTables = ["__EFMigrationsHistory"];

    public static async Task<RestoreDrillResult> RunAsync(
        string liveConnectionString,
        string snapshotDir,
        string scratchDatabase = "flowledger_drill",
        bool keep = false,
        CancellationToken ct = default)
    {
        var problems = new List<string>();
        var live = new NpgsqlConnectionStringBuilder(liveConnectionString);
        if (string.Equals(live.Database, scratchDatabase, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to drill into the live database.");
        }

        var verify = await DatabaseSnapshotService.VerifyAsync(snapshotDir, ct);
        if (!verify.Ok)
        {
            return new RestoreDrillResult(false, scratchDatabase, false, [], verify.Problems);
        }

        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(
            await File.ReadAllTextAsync(Path.Combine(snapshotDir, DatabaseSnapshotService.ManifestFileName), ct))!;

        var admin = new NpgsqlConnectionStringBuilder(liveConnectionString) { Database = "postgres", Pooling = false };
        var scratch = new NpgsqlConnectionStringBuilder(liveConnectionString) { Database = scratchDatabase, Pooling = false };

        await using (var conn = new NpgsqlConnection(admin.ConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{scratchDatabase}\" WITH (FORCE)", conn);
            await drop.ExecuteNonQueryAsync(ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{scratchDatabase}\"", conn);
            await create.ExecuteNonQueryAsync(ct);
        }

        var results = new List<DrillTableResult>();
        try
        {
            await MigrateAsync(scratch.ConnectionString, manifest.AppliedMigrations.LastOrDefault(), ct);
            await EnsureAdHocTablesAsync(scratch.ConnectionString, ct);

            results.AddRange(await LoadTablesAsync(scratch.ConnectionString, snapshotDir, manifest, problems, ct));
        }
        catch (Exception ex)
        {
            problems.Add("Restore failed: " + ex.Message);
        }
        finally
        {
            if (!keep)
            {
                await using var conn = new NpgsqlConnection(admin.ConnectionString);
                await conn.OpenAsync(CancellationToken.None);
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{scratchDatabase}\" WITH (FORCE)", conn);
                await drop.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }

        return new RestoreDrillResult(problems.Count == 0, scratchDatabase, keep, results, problems);
    }

    /// <summary>COPYs every manifest table (except migration history/settings) from CSV into the target and checks row counts.</summary>
    internal static async Task<List<DrillTableResult>> LoadTablesAsync(
        string connectionString, string snapshotDir, SnapshotManifest manifest, List<string> problems, CancellationToken ct)
    {
        // The manifest and CSV headers come from an uploaded bundle: only plain identifiers are ever interpolated into SQL.
        foreach (var t in manifest.Tables)
        {
            if (!SafeIdentifier.IsMatch(t.Table))
            {
                throw new InvalidOperationException($"Refusing bundle: unsafe table name '{t.Table}'.");
            }
        }

        var results = new List<DrillTableResult>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using (var rep = new NpgsqlCommand("SET session_replication_role = replica", conn))
        {
            await rep.ExecuteNonQueryAsync(ct); // skip FK checks while loading; counts verify completeness
        }

        foreach (var t in manifest.Tables.Where(t => !SkippedTables.Contains(t.Table)))
        {
            var file = Path.Combine(snapshotDir, t.Table + ".csv");
            var columns = (await File.ReadLinesAsync(file, ct).FirstAsync(ct)).Split(',').Select(c => c.Trim('"')).ToList();
            var bad = columns.FirstOrDefault(c => !SafeIdentifier.IsMatch(c));
            if (bad is not null)
            {
                throw new InvalidOperationException($"Refusing bundle: unsafe column name '{bad}' in {t.Table}.csv.");
            }

            var header = columns.Select(c => "\"" + c + "\"");
            await using (var import = await conn.BeginTextImportAsync(
                $"COPY \"{t.Table}\" ({string.Join(",", header)}) FROM STDIN (FORMAT csv, HEADER true)", ct))
            {
                var buf = new char[81920];
                using var reader = new StreamReader(file);
                int n;
                while ((n = await reader.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    await import.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }

            await using var count = new NpgsqlCommand($"SELECT count(*) FROM \"{t.Table}\"", conn);
            var restored = (long)(await count.ExecuteScalarAsync(ct))!;
            var ok = restored == t.Rows;
            results.Add(new DrillTableResult(t.Table, t.Rows, restored, ok));
            if (!ok)
            {
                problems.Add($"{t.Table}: expected {t.Rows} rows, restored {restored}");
            }
        }

        // COPY wrote explicit ids, so every serial/identity sequence is still at 1. Without this the next insert
        // (ledger row, backup run, ...) would hit a duplicate key after a restore/import.
        await using (var seq = new NpgsqlCommand(ResetSequencesSql, conn))
        {
            await seq.ExecuteNonQueryAsync(ct);
        }

        return results;
    }

    private const string ResetSequencesSql = """
        DO $$
        DECLARE r record;
        BEGIN
          FOR r IN
            SELECT c.table_name AS tbl, c.column_name AS col,
                   pg_get_serial_sequence(quote_ident(c.table_name), c.column_name) AS seq
            FROM information_schema.columns c
            WHERE c.table_schema = 'public'
          LOOP
            IF r.seq IS NOT NULL THEN
              EXECUTE format('SELECT setval(%L, COALESCE((SELECT max(%I) FROM %I), 0) + 1, false)', r.seq, r.col, r.tbl);
            END IF;
          END LOOP;
        END $$;
        """;

    internal static async Task MigrateAsync(string connectionString, string? targetMigration, CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<FlowLedgerDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name))
            .Options;
        await using var db = new FlowLedgerDbContext(options, new NoOpDomainEventDispatcher(), UnscopedTenantContext.Instance);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration, ct);
    }

    /// <summary>Tables created outside EF migrations (system_settings, provider_api_calls) so they can be restored too.</summary>
    internal static async Task EnsureAdHocTablesAsync(string connectionString, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(AdHocSchema.AllSql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
