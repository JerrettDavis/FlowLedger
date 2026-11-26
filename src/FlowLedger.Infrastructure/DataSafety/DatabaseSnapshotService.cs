using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>One table inside a snapshot.</summary>
public sealed record SnapshotTable(string Table, long Rows, string Sha256, long Bytes);

/// <summary>Manifest written next to the CSV files of a snapshot.</summary>
public sealed record SnapshotManifest(
    int Version,
    DateTimeOffset CreatedUtc,
    string Database,
    string Reason,
    IReadOnlyList<string> AppliedMigrations,
    IReadOnlyList<SnapshotTable> Tables,
    Guid? TenantId = null);

public sealed record SnapshotVerification(bool Ok, IReadOnlyList<string> Problems);

/// <summary>
/// Tool-free, schema-independent logical snapshot of every public table: each table is streamed
/// with COPY ... TO STDOUT (CSV) inside ONE repeatable-read transaction (so the snapshot is consistent),
/// hashed, and described by a manifest. Needs only Npgsql, so it works on any host (no pg_dump required)
/// and is used before migrations, by the scheduled backup job and by restore drills.
/// Nothing in here ever deletes or overwrites existing snapshots.
/// </summary>
public sealed class DatabaseSnapshotService
{
    public const string ManifestFileName = "manifest.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _connectionString;

    public DatabaseSnapshotService(string connectionString) => _connectionString = connectionString;

    public static string DefaultBackupRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".flowledger", "backups");

    /// <summary>Creates <c>{backupRoot}/{prefix}-{yyyyMMdd-HHmmss}/</c>, writes it, verifies it, and returns its path. Throws on any failure.</summary>
    /// <param name="tenantId">When set (#139), exports ONLY that tenant's rows: every table with a <c>tenant_id</c> column filtered to it, plus
    /// child tables reached through a foreign key (for example transaction splits). Tables with no tenant data are skipped. The manifest records the
    /// tenant, and <see cref="SnapshotImport"/> refuses such a bundle (it is for portability and review, not restore).</param>
    public async Task<(string Directory, SnapshotManifest Manifest)> CreateAsync(
        string backupRoot, string reason, string prefix = "snapshot", CancellationToken ct = default, Guid? tenantId = null)
    {
        var dir = Path.Combine(backupRoot, $"{prefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        var attempt = 1;
        while (Directory.Exists(dir)) { dir = Path.Combine(backupRoot, $"{prefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{++attempt}"); }
        Directory.CreateDirectory(dir);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);

        var tables = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE' ORDER BY 1", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                tables.Add(r.GetString(0));
            }
        }

        var applied = new List<string>();
        if (tables.Contains("__EFMigrationsHistory"))
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY 1", conn, tx);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                applied.Add(r.GetString(0));
            }
        }

        // Table name -> the SELECT that yields the rows to export (whole table, or only the tenant's rows).
        var selects = new Dictionary<string, string>(StringComparer.Ordinal);
        if (tenantId is { } scope)
        {
            foreach (var (table, select) in await TenantSelectsAsync(conn, tx, tables, scope, ct))
            {
                selects[table] = select;
            }

            tables = [.. tables.Where(selects.ContainsKey)];
        }
        else
        {
            foreach (var table in tables)
            {
                selects[table] = $"SELECT * FROM {Quote(table)}";
            }
        }

        var entries = new List<SnapshotTable>();
        foreach (var table in tables)
        {
            var file = Path.Combine(dir, table + ".csv");
            await using (var export = await conn.BeginTextExportAsync(
                $"COPY ({selects[table]}) TO STDOUT (FORMAT csv, HEADER true)", ct))
            await using (var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                var buf = new char[81920];
                int n;
                while ((n = await export.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    await w.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }

            var (rows, sha, bytes) = await InspectAsync(file, ct);
            entries.Add(new SnapshotTable(table, rows, sha, bytes));
        }

        // Cross-check against the database itself, inside the same snapshot.
        foreach (var e in entries)
        {
            await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM ({selects[e.Table]}) AS q", conn, tx);
            var live = (long)(await cmd.ExecuteScalarAsync(ct))!;
            if (live != e.Rows)
            {
                throw new InvalidOperationException($"Snapshot of '{e.Table}' has {e.Rows} rows but the database reports {live}.");
            }
        }
        await tx.CommitAsync(ct);

        var manifest = new SnapshotManifest(1, DateTimeOffset.UtcNow, conn.Database ?? "", reason, applied, entries, tenantId);
        await File.WriteAllTextAsync(Path.Combine(dir, ManifestFileName), JsonSerializer.Serialize(manifest, Json), ct);

        var check = await VerifyAsync(dir, ct);
        if (!check.Ok)
        {
            throw new InvalidOperationException("Snapshot failed verification: " + string.Join("; ", check.Problems));
        }
        return (dir, manifest);
    }

    /// <summary>
    /// The per-table SELECTs of a tenant-scoped export. Table and column names come from the database catalog and are validated and quoted;
    /// the tenant id is a <see cref="Guid"/> rendered in its fixed hex form, so no caller text reaches the SQL.
    /// </summary>
    private static async Task<List<(string Table, string Select)>> TenantSelectsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, List<string> tables, Guid tenant, CancellationToken ct)
    {
        var literal = "'" + tenant.ToString("D") + "'::uuid";
        var scoped = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.columns WHERE table_schema = 'public' AND column_name = 'tenant_id'", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                scoped.Add(r.GetString(0));
            }
        }

        var result = new List<(string, string)>();
        foreach (var t in tables.Where(scoped.Contains))
        {
            result.Add((t, $"SELECT * FROM {Quote(t)} WHERE tenant_id = {literal}"));
        }

        // Child tables with no tenant column of their own (owned collections): rows whose parent belongs to the tenant.
        await using (var cmd = new NpgsqlCommand(
            @"SELECT c.relname, p.relname,
                     (SELECT string_agg(quote_ident(a.attname), ',' ORDER BY k.ord) FROM unnest(con.conkey) WITH ORDINALITY k(attnum, ord)
                        JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum),
                     (SELECT string_agg(quote_ident(a.attname), ',' ORDER BY k.ord) FROM unnest(con.confkey) WITH ORDINALITY k(attnum, ord)
                        JOIN pg_attribute a ON a.attrelid = con.confrelid AND a.attnum = k.attnum)
              FROM pg_constraint con
              JOIN pg_class c ON c.oid = con.conrelid JOIN pg_class p ON p.oid = con.confrelid
              JOIN pg_namespace n ON n.oid = c.relnamespace
              WHERE con.contype = 'f' AND n.nspname = 'public'", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            var childSelects = new Dictionary<string, string>(StringComparer.Ordinal);
            while (await r.ReadAsync(ct))
            {
                var child = r.GetString(0);
                var parent = r.GetString(1);
                if (scoped.Contains(child) || !scoped.Contains(parent) || !tables.Contains(child) || childSelects.ContainsKey(child))
                {
                    continue;
                }

                var childCols = r.GetString(2).Split(',').Select(c => "t." + c);
                var parentCols = r.GetString(3).Split(',').Select(c => "p." + c);
                childSelects[child] =
                    $"SELECT t.* FROM {Quote(child)} t WHERE EXISTS (SELECT 1 FROM {Quote(parent)} p " +
                    $"WHERE ({string.Join(", ", parentCols)}) = ({string.Join(", ", childCols)}) AND p.tenant_id = {literal})";
            }

            result.AddRange(childSelects.Select(kv => (kv.Key, kv.Value)));
        }

        return result;
    }

    /// <summary>Re-reads every file and compares hash, size and record count with the manifest.</summary>
    public static async Task<SnapshotVerification> VerifyAsync(string dir, CancellationToken ct = default)
    {
        var problems = new List<string>();
        var manifestPath = Path.Combine(dir, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return new SnapshotVerification(false, ["manifest.json is missing"]);
        }

        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(await File.ReadAllTextAsync(manifestPath, ct));
        if (manifest is null)
        {
            return new SnapshotVerification(false, ["manifest.json is unreadable"]);
        }

        foreach (var t in manifest.Tables)
        {
            var file = Path.Combine(dir, t.Table + ".csv");
            if (!File.Exists(file)) { problems.Add($"{t.Table}.csv is missing"); continue; }
            var (rows, sha, bytes) = await InspectAsync(file, ct);
            if (sha != t.Sha256)
            {
                problems.Add($"{t.Table}.csv hash mismatch");
            }
            if (bytes != t.Bytes)
            {
                problems.Add($"{t.Table}.csv size mismatch");
            }
            if (rows != t.Rows)
            {
                problems.Add($"{t.Table}.csv has {rows} rows, manifest says {t.Rows}");
            }
        }
        return new SnapshotVerification(problems.Count == 0, problems);
    }

    /// <summary>Counts live rows per table (for comparing a snapshot with the database).</summary>
    public async Task<IReadOnlyDictionary<string, long>> CountLiveRowsAsync(IEnumerable<string> tables, CancellationToken ct = default)
    {
        var result = new Dictionary<string, long>();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        foreach (var t in tables)
        {
            await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {Quote(t)}", conn);
            result[t] = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        return result;
    }

    private static string Quote(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";

    /// <summary>One pass: SHA-256, byte length and CSV record count (quote-aware; header excluded).</summary>
    private static async Task<(long Rows, string Sha256, long Bytes)> InspectAsync(string file, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buf = new byte[81920];
        long bytes = 0, records = 0;
        var inQuotes = false;
        var lastWasNewline = true;
        int n;
        while ((n = await fs.ReadAsync(buf, ct)) > 0)
        {
            sha.TransformBlock(buf, 0, n, null, 0);
            bytes += n;
            for (var i = 0; i < n; i++)
            {
                var b = buf[i];
                if (b == (byte)'"') { inQuotes = !inQuotes; lastWasNewline = false; }
                else if (b == (byte)'\n' && !inQuotes) { records++; lastWasNewline = true; }
                else
                {
                    lastWasNewline = false;
                }
            }
        }
        sha.TransformFinalBlock([], 0, 0);
        if (!lastWasNewline)
        {
            records++; // unterminated final record
        }
        var rows = Math.Max(0, records - 1); // minus header line
        return (rows, Convert.ToHexString(sha.Hash!).ToLowerInvariant(), bytes);
    }
}
