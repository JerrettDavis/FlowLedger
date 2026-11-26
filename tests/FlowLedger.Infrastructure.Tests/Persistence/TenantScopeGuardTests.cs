using System.Text.RegularExpressions;
using FlowLedger.Infrastructure.DataSafety;
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
        var globalTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // OpenIddict / identity style tables would be listed here with a reason; none exist in the EF model today.
        };
        var offenders = ctx.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null && !e.IsOwned())
            .Where(e => e.GetProperties().All(p => p.GetColumnName() != "tenant_id"))
            .Select(e => e.GetTableName()!)
            .Where(t => !globalTables.Contains(t))
            .Distinct()
            .ToList();

        offenders.Should().BeEmpty("every EF table must carry tenant_id (or be added to the global allowlist with a reason)");
    }

    // ── ad-hoc tables ────────────────────────────────────────────────────────

    /// <summary>Ad-hoc tables that are legitimately not per tenant, with the reason.</summary>
    private static readonly Dictionary<string, string> GlobalAdHocTables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["system_settings"] = "host-wide key/value settings",
        ["provider_api_calls"] = "provider API call accounting for the whole host (cost control)",
        ["backup_runs"] = "host backup history",
        ["job_runs"] = "host-wide background job run history (scheduled jobs are not per tenant)",
        ["tenants"] = "the tenant registry itself (#169), keyed by id; erasure keeps its row as the audit record",
        ["webhook_replays"] = "webhook body hashes and timestamps only (#152), no tenant data",
    };

    [Fact]
    public void Every_ad_hoc_table_has_a_tenant_id_column_or_is_listed_as_global()
    {
        var offenders = new List<string>();
        foreach (Match m in Regex.Matches(AdHocSchema.AllSql, @"CREATE TABLE IF NOT EXISTS\s+(?<t>\w+)\s*\((?<body>.*?)\);", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var table = m.Groups["t"].Value;
            if (!m.Groups["body"].Value.Contains("tenant_id", StringComparison.OrdinalIgnoreCase) && !GlobalAdHocTables.ContainsKey(table))
            {
                offenders.Add(table);
            }
        }

        offenders.Should().BeEmpty("an ad-hoc table without tenant_id cannot be tenant-scoped; list it in GlobalAdHocTables with a reason if it is host-wide");
    }

    // ── (c) raw SQL must mention tenant_id ───────────────────────────────────

    /// <summary>File name to reason. Statements in these files are host-wide or filter by another tenant-derived key.</summary>
    private static readonly Dictionary<string, string> RawSqlAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BackupRunner.cs"] = "host backup history (backup_runs is host-wide)",
        ["DatabaseSnapshotService.cs"] = "whole-database snapshot/restore, never per tenant",
        ["SnapshotImport.cs"] = "whole-database restore, never per tenant",
        ["SnapshotRestoreDrill.cs"] = "whole-database restore drill, never per tenant",
        ["NpgsqlJobRunStore.cs"] = "host-wide background job run history, no tenant column",
        ["NpgsqlProviderCallLedger.cs"] = "provider API call accounting for the whole host (cost control), no tenant column",
        ["DataEnvironmentGuard.cs"] = "host-wide environment fingerprint in system_settings",
        ["EfTenantErasure.cs"] = "tenant erasure (#169): every DELETE is keyed by the tenant column; the other statements read schema metadata or the tenant registry",
        ["NpgsqlTenantRegistry.cs"] = "the tenant registry (#169) is keyed by id, not tenant_id",
        ["NpgsqlWebhookReplayStore.cs"] = "webhook replay hashes (#152): no tenant data",
        ["DataRetention.cs"] = "host-wide maintenance: blanks old merge snapshots and deletes expired link codes across all tenants (#181)",
        ["ReadinessChecks.cs"] = "host-wide readiness probes (migration history, backup freshness); no tenant data is returned",
    };

    /// <summary>Literal fragments that are not complete statements: (file, text in the literal, reason).</summary>
    private static readonly (string File, string Contains, string Why)[] FragmentAllowlist =
    [
        ("NpgsqlUserDirectory.cs", "INSERT INTO tenants", "registers a tenant in the registry (#169), which is keyed by id"),
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
}
