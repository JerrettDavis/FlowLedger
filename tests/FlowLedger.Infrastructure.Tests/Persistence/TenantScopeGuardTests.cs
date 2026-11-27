using System.Text.RegularExpressions;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.HostStores;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>
/// Issue #136 (b) and (c): structural guards that need no database. A tenant table without a query filter, an ad-hoc table without
/// tenant_id, or a raw SQL statement that never mentions tenant_id must be a conscious, listed exception, not an accident.
/// </summary>
public sealed class TenantScopeGuardTests
{
    private static FlowLedgerDbContext Context() =>
        new(new DbContextOptionsBuilder<FlowLedgerDbContext>()
                .UseNpgsql("Host=localhost;Database=never_connected;Username=x;Password=x")
                .Options,
            new NoOpDomainEventDispatcher(),
            TestTenantContext.New());

    // ── (b) every EF entity with a tenant column has a query filter ──────────

    /// <summary>Entities with a tenant column that are deliberately unfiltered (none today). Add with a reason.</summary>
    private static readonly Dictionary<string, string> UnfilteredEntities = new();

    [Fact]
    public void Every_EF_entity_with_a_tenant_id_has_a_query_filter()
    {
        using var ctx = Context();
        var tenantOwned = ctx.Model.GetEntityTypes().Where(e => !e.IsOwned() && e.FindProperty("TenantId") is not null).ToList();
        tenantOwned.Count.Should().BeGreaterThanOrEqualTo(8, "the guard must actually find the tenant-owned entities, or it proves nothing");
        var offenders = tenantOwned
            .Where(e => !e.GetDeclaredQueryFilters().Any())
            .Select(e => e.ClrType.Name)
            .Where(n => !UnfilteredEntities.ContainsKey(n))
            .ToList();

        offenders.Should().BeEmpty("a tenant-owned entity without HasQueryFilter leaks across tenants on any plain query");
    }

    [Fact]
    public void Every_EF_table_has_a_tenant_id_column_or_is_listed_as_global()
    {
        using var ctx = Context();
        var globalTables = FlowLedger.Infrastructure.Persistence.HostWideEntities.Tables;
        var offenders = ctx.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null && !e.IsOwned())
            .Where(e => e.GetProperties().All(p => p.GetColumnName() != "tenant_id"))
            .Select(e => e.GetTableName()!)
            .Where(t => !globalTables.ContainsKey(t))
            .Distinct()
            .ToList();

        offenders.Should().BeEmpty("every EF table must carry tenant_id (or be added to the global allowlist with a reason)");
    }

    // ── host-wide tables (#154) ──────────────────────────────────────────────

    [Fact]
    public void Host_wide_allow_list_matches_the_entities_without_a_tenant_id_or_filter_exactly()
    {
        using var ctx = Context();
        var noTenant = ctx.Model.GetEntityTypes()
            .Where(e => !e.IsOwned() && e.BaseType is null && e.GetTableName() is not null)
            .Where(e => e.FindProperty("TenantId") is null)
            .ToList();

        var unlisted = noTenant.Select(e => e.GetTableName()!).Where(t => !HostWideEntities.Tables.ContainsKey(t)).ToList();
        unlisted.Should().BeEmpty("an entity without TenantId is host-wide: list it in HostWideEntities with a reason, or give it a tenant");

        var stale = HostWideEntities.Tables.Keys.Where(t => noTenant.All(e => e.GetTableName() != t)).ToList();
        stale.Should().BeEmpty("HostWideEntities lists a table that is not (or no longer) a host-wide entity");

        noTenant.Where(e => e.GetDeclaredQueryFilters().Any()).Select(e => e.ClrType.Name).Should().BeEmpty(
            "a host-wide entity has no tenant to filter by");
    }

    [Fact]
    public void Host_wide_DbSets_are_only_reached_through_HostDb()
    {
        using var ctx = Context();
        var dbSets = typeof(FlowLedgerDbContext)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Where(p => ctx.Model.FindEntityType(p.PropertyType.GetGenericArguments()[0]) is { } e
                && e.GetTableName() is { } table && HostWideEntities.Tables.ContainsKey(table))
            .Select(p => p.Name)
            .ToList();
        dbSets.Should().NotBeEmpty("the guard must find the host-wide DbSets, or it proves nothing");

        var root = InfrastructureRoot();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/Migrations/") || relative.StartsWith("Migrations/", StringComparison.Ordinal)
                || relative.Contains("/obj/") || relative.Contains("/bin/") || relative == "Persistence/FlowLedgerDbContext.cs")
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var touches = dbSets.Where(n => Regex.IsMatch(text, @"\.\s*" + n + @"\b")).ToList();
            if (touches.Count > 0 && !text.Contains("HostDb", StringComparison.Ordinal) && !text.Contains("FlowLedgerDbContext", StringComparison.Ordinal))
            {
                offenders.Add($"{relative}: {string.Join(", ", touches)}");
            }
        }

        offenders.Should().BeEmpty("host-wide tables carry no tenant filter, so only HostDb-based stores may touch them");

        // Stricter: a tenant-scoped store (anything taking ITenantContext) must not touch them at all.
        var scoped = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/Migrations/") || relative.Contains("/obj/") || relative.Contains("/bin/") || relative == "Persistence/FlowLedgerDbContext.cs")
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (text.Contains("ITenantContext tenant", StringComparison.Ordinal) && dbSets.Any(n => Regex.IsMatch(text, @"\.\s*" + n + @"\b")))
            {
                scoped.Add(relative);
            }
        }

        scoped.Should().BeEmpty("a tenant-scoped store must not read or write host-wide tables");
    }

    // ── (c) raw SQL must mention tenant_id ───────────────────────────────────

    /// <summary>File name to reason. Statements in these files are host-wide or filter by another tenant-derived key.</summary>
    private static readonly Dictionary<string, string> RawSqlAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DatabaseSnapshotService.cs"] = "whole-database snapshot/restore, never per tenant",
        ["SnapshotImport.cs"] = "whole-database restore, never per tenant",
        ["SnapshotRestoreDrill.cs"] = "whole-database restore drill, never per tenant",
        ["DataEnvironmentGuard.cs"] = "host-wide environment fingerprint in system_settings",
        ["EfTenantErasure.cs"] = "tenant erasure (#169): every DELETE is keyed by the tenant column; the other statements read schema metadata or the tenant registry",
        ["EfWebhookReplayStore.cs"] = "webhook replay hashes (#152): host-wide, no tenant data",
        ["DataRetention.cs"] = "host-wide maintenance: blanks old merge snapshots and deletes expired link codes across all tenants (#181)",
        ["ReadinessChecks.cs"] = "host-wide readiness probes (migration history, backup freshness); no tenant data is returned",
        ["JobFireMarks.cs"] = "host-wide last cron fire time per background job (#187); no tenant data",
    };

    /// <summary>Literal fragments that are not complete statements: (file, text in the literal, reason).</summary>
    private static readonly (string File, string Contains, string Why)[] FragmentAllowlist =
    [
        ("EfUserDirectory.cs", "INSERT INTO tenants", "registers a tenant in the registry (#169), which is keyed by id"),
    ];

    private static string InfrastructureRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FlowLedger.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull();
        return Path.Combine(dir!.FullName, "src", "FlowLedger.Infrastructure");
    }

    private static readonly Regex SqlLiteral = new(
        "\"\"\"(?<raw>.*?)\"\"\"|(?<!\")\\$?@?\"(?<str>(?:[^\"\\\\]|\\\\.)*)\"",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex SqlStatement = new(
        @"\b(SELECT\s.+\sFROM|UPDATE\s+\w+\s+SET|DELETE\s+FROM|INSERT\s+INTO)\b", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void Every_raw_SQL_statement_in_Infrastructure_mentions_tenant_id_or_is_allowlisted()
    {
        var root = InfrastructureRoot();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/Migrations/") || relative.StartsWith("Migrations/", StringComparison.Ordinal)
                || relative.Contains("/obj/") || relative.Contains("/bin/") || RawSqlAllowlist.ContainsKey(Path.GetFileName(file)))
            {
                continue;
            }

            // Adjacent literals are one statement: "SELECT ... " + "WHERE tenant_id = @t".
            var text = Regex.Replace(File.ReadAllText(file), @"""\s*\+\s*\$?""", string.Empty);
            foreach (Match lit in SqlLiteral.Matches(text))
            {
                var body = lit.Groups["raw"].Success ? lit.Groups["raw"].Value : lit.Groups["str"].Value;
                if (SqlStatement.IsMatch(body) && !body.Contains("tenant_id", StringComparison.OrdinalIgnoreCase)
                    && !FragmentAllowlist.Any(f => f.File == Path.GetFileName(file) && body.Contains(f.Contains, StringComparison.Ordinal)))
                {
                    var line = text[..lit.Index].Count(c => c == '\n') + 1;
                    offenders.Add($"{relative}:{line}: {Regex.Replace(body.Trim(), @"\s+", " ")[..Math.Min(90, Regex.Replace(body.Trim(), @"\s+", " ").Length)]}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "raw SQL bypasses EF query filters; every statement must filter or key by tenant_id, or its file must be in RawSqlAllowlist with a reason");
    }

    [Fact]
    public void Raw_SQL_allowlist_has_no_stale_entries()
    {
        // An allowlisted file must still hold at least one statement that needs the exemption; otherwise the entry only hides future mistakes.
        var root = InfrastructureRoot();
        var filesNeedingExemption = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/Migrations/") || relative.Contains("/obj/") || relative.Contains("/bin/"))
            {
                continue;
            }

            var text = Regex.Replace(File.ReadAllText(file), @"""\s*\+\s*\$?""", string.Empty);
            if (SqlLiteral.Matches(text).Select(l => l.Groups["raw"].Success ? l.Groups["raw"].Value : l.Groups["str"].Value)
                .Any(body => SqlStatement.IsMatch(body) && !body.Contains("tenant_id", StringComparison.OrdinalIgnoreCase)))
            {
                filesNeedingExemption.Add(Path.GetFileName(file));
            }
        }

        RawSqlAllowlist.Keys.Where(f => !filesNeedingExemption.Contains(f)).Should().BeEmpty(
            "these files no longer contain tenant-less raw SQL; remove them from RawSqlAllowlist");
    }
}
