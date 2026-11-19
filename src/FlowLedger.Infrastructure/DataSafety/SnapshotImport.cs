using System.IO.Compression;
using System.Text.Json;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Export/import of ALL user data as a portable zip (one CSV per table plus manifest.json with hashes and row counts).
/// Export is just a verified snapshot, zipped. Import (disaster recovery / moving machines) is deliberately
/// restricted to an EMPTY database so it can never overwrite or merge into existing data.
/// </summary>
public static class SnapshotImport
{
    // Never imported: schema history (migrations rebuild it) and the environment marker (owned by the startup guard).
    private static readonly string[] NotImported = ["__EFMigrationsHistory", "system_settings"];

    public static async Task<string> ExportZipAsync(string connectionString, string workDir, CancellationToken ct = default)
    {
        var (dir, _) = await new DatabaseSnapshotService(connectionString).CreateAsync(workDir, "export", "export", ct);
        var zip = dir + ".zip";
        ZipFile.CreateFromDirectory(dir, zip, CompressionLevel.Optimal, includeBaseDirectory: false);
        Directory.Delete(dir, recursive: true); // only the zip remains; no loose unencrypted CSV copy is left behind
        return zip;
    }

    private const long MaxUncompressedBytes = 8L * 1024 * 1024 * 1024;

    public static async Task<RestoreDrillResult> ImportZipAsync(
        string targetConnectionString, Stream zip, string workDir, CancellationToken ct = default)
    {
        var dir = Path.Combine(workDir, "import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true))
            {
                // Zip-bomb guard: refuse bundles whose declared uncompressed size is implausible.
                if (archive.Entries.Sum(x => x.Length) > MaxUncompressedBytes)
                {
                    return Fail($"Bundle expands to more than {MaxUncompressedBytes / (1024 * 1024 * 1024)} GB; refusing to extract it.");
                }

                foreach (var entry in archive.Entries)
                {
                    // Flat bundle only; reject anything with a path (zip-slip).
                    if (entry.FullName.Contains('/') || entry.FullName.Contains('\\') || entry.FullName.Contains(".."))
                    {
                        return Fail("Bundle contains an unexpected path: " + entry.FullName);
                    }

                    entry.ExtractToFile(Path.Combine(dir, entry.Name), overwrite: false);
                }
            }

            var verify = await DatabaseSnapshotService.VerifyAsync(dir, ct);
            if (!verify.Ok)
            {
                return new RestoreDrillResult(false, "", false, [], verify.Problems);
            }

            var manifest = JsonSerializer.Deserialize<SnapshotManifest>(
                await File.ReadAllTextAsync(Path.Combine(dir, DatabaseSnapshotService.ManifestFileName), ct))!;

            // Safety: only an empty database may receive an import.
            var existing = await UserRowCountAsync(targetConnectionString, ct);
            if (existing > 0)
            {
                return Fail($"Refusing to import: the target database already contains {existing} rows. Import is only allowed into an empty database.");
            }

            await SnapshotRestoreDrill.MigrateAsync(targetConnectionString, manifest.AppliedMigrations.LastOrDefault(), ct);
            await SnapshotRestoreDrill.EnsureAdHocTablesAsync(targetConnectionString, ct);

            var problems = new List<string>();
            var toLoad = manifest with { Tables = manifest.Tables.Where(t => !NotImported.Contains(t.Table)).ToList() };
            var results = await SnapshotRestoreDrill.LoadTablesAsync(targetConnectionString, dir, toLoad, problems, ct);

            // Bring the schema forward to the current model.
            await SnapshotRestoreDrill.MigrateAsync(targetConnectionString, null, ct);

            return new RestoreDrillResult(problems.Count == 0, "", false, results, problems);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // best effort temp cleanup
            }
        }

        static RestoreDrillResult Fail(string why) => new(false, "", false, [], [why]);
    }

    /// <summary>Rows across every data table (ignoring bookkeeping tables); a freshly migrated database has none.</summary>
    private static async Task<long> UserRowCountAsync(string connectionString, CancellationToken ct)
    {
        var ignore = new[] { "__EFMigrationsHistory", "system_settings", "backup_runs", "provider_api_calls" };
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        var tables = new List<string>();
        await using (var list = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE'", conn))
        await using (var r = await list.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                tables.Add(r.GetString(0));
            }
        }

        long total = 0;
        foreach (var table in tables.Where(t => !ignore.Contains(t)))
        {
            await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM \"{table}\"", conn);
            total += (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        return total;
    }
}
