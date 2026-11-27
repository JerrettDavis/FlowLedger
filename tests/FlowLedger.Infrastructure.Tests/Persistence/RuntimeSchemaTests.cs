using System.Text.RegularExpressions;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>#154 / #87: the schema is owned by EF migrations alone. Nothing may create or alter tables at runtime.</summary>
public sealed class NoRuntimeSchemaTests
{
    private static string InfrastructureRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FlowLedger.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull();
        return Path.Combine(dir!.FullName, "src");
    }

    [Fact]
    public void No_source_file_outside_the_migrations_creates_or_alters_tables_or_indexes()
    {
        var root = InfrastructureRoot();
        var ddl = new Regex(@"\b(CREATE\s+(UNIQUE\s+)?(TABLE|INDEX)|ALTER\s+TABLE|DROP\s+TABLE)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/Migrations/") || relative.Contains("/obj/") || relative.Contains("/bin/"))
            {
                continue;
            }

            var code = string.Join('\n', File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            if (ddl.IsMatch(code))
            {
                offenders.Add(relative);
            }
        }

        offenders.Should().BeEmpty("tables and indexes come from EF migrations only; runtime CREATE TABLE IF NOT EXISTS has two owners and drifts");
    }

    [Fact]
    public void Direct_Npgsql_use_is_confined_to_database_tooling()
    {
        // Stores go through EF. Raw NpgsqlConnection/NpgsqlCommand remain only where a store is not the point: whole-database
        // snapshot/restore, advisory locks, version probes, retention sweeps, secret re-encryption and readiness probes.
        var allowed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FlowLedger.Infrastructure/DataSafety/DatabaseSnapshotService.cs"] = "whole-database snapshot",
            ["FlowLedger.Infrastructure/DataSafety/SnapshotImport.cs"] = "whole-database import",
            ["FlowLedger.Infrastructure/DataSafety/SnapshotRestoreDrill.cs"] = "whole-database restore drill",
            ["FlowLedger.Infrastructure/DataSafety/PostgresAdvisoryLock.cs"] = "advisory locks need a dedicated connection",
            ["FlowLedger.Infrastructure/DataSafety/PostgresVersionGuard.cs"] = "server version probe before the schema exists",
            ["FlowLedger.Infrastructure/DataSafety/DataRetention.cs"] = "host-wide retention sweeps across tenants",
            ["FlowLedger.Infrastructure/DataSafety/SecretReencryptor.cs"] = "bulk secret re-encryption",
            ["FlowLedger.Infrastructure/Health/ReadinessChecks.cs"] = "readiness probes",
        };

        var root = InfrastructureRoot();
        var users = Directory.EnumerateFiles(Path.Combine(root, "FlowLedger.Infrastructure"), "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !f.Contains("/obj/") && !f.Contains("/bin/") && !f.Contains("/Migrations/"))
            .Where(f => Regex.IsMatch(File.ReadAllText(Path.Combine(root, f)), @"\bNpgsql(Connection|Command|DataSource)\b"))
            .ToList();

        users.Where(f => !allowed.ContainsKey(f)).Should().BeEmpty("a store must use EF, not a raw Npgsql connection");
        allowed.Keys.Where(f => !users.Contains(f)).Should().BeEmpty("remove files from the allow-list once they stop using Npgsql directly");
    }
}

[Collection("Integration")]
[Trait("Invariant", "INV-11")]
public sealed class SnapshotRestoreAllTablesTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fl-adhoc-" + Guid.NewGuid().ToString("N"));

    public SnapshotRestoreAllTablesTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    [DockerFact]
    public async Task Snapshot_with_every_formerly_ad_hoc_table_restores()
    {
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var ins = new NpgsqlCommand(
                "INSERT INTO merchant_aliases (tenant_id, pattern, alias) VALUES (gen_random_uuid(), 'amzn', 'Amazon')", conn);
            await ins.ExecuteNonQueryAsync();
        }

        var (dir, manifest) = await new DatabaseSnapshotService(_fixture.ConnectionString).CreateAsync(_root, "adhoc");
        foreach (var t in new[]
        {
            "merchant_aliases", "recurring_dismissals", "suspicious_dismissals", "savings_goals", "category_rules", "budgets",
            "provider_api_calls", "backup_runs", "audit_events", "job_runs", "webhook_replays", "tenants", "app_users", "app_user_link_codes", "system_settings",
        })
        {
            manifest.Tables.Should().Contain(x => x.Table == t);
        }

        var drill = await SnapshotRestoreDrill.RunAsync(_fixture.ConnectionString, dir, "flowledger_drill_adhoc");

        drill.Ok.Should().BeTrue(string.Join("; ", drill.Problems));
        drill.Tables.Should().Contain(t => t.Table == "merchant_aliases" && t.Ok && t.RestoredRows == 1);
    }

    [DockerFact]
    public async Task A_snapshot_taken_before_a_table_moved_into_a_migration_still_restores()
    {
        // Old snapshot: its schema level (the last migration it records) predates the migration that creates job_runs, yet the
        // snapshot holds job_runs rows because the old store created the table at runtime.
        var (dir, manifest) = await new DatabaseSnapshotService(_fixture.ConnectionString).CreateAsync(_root, "old");
        var old = manifest with
        {
            AppliedMigrations = manifest.AppliedMigrations.Where(m => !m.Contains("MoveAuditJobRunsWebhookReplaysToEf", StringComparison.Ordinal)
                && !m.Contains("MoveUserDirectoryAndTenantsToEf", StringComparison.Ordinal) && !m.Contains("MoveSystemSettingsToEf", StringComparison.Ordinal)
                && !m.Contains("MoveProviderCallsAndBackupRunsToEf", StringComparison.Ordinal)).ToList(),
        };
        await File.WriteAllTextAsync(Path.Combine(dir, DatabaseSnapshotService.ManifestFileName),
            System.Text.Json.JsonSerializer.Serialize(old));

        // The manifest is rewritten, so the verification hash of the manifest itself is not part of the check (only the CSV hashes are).
        var drill = await SnapshotRestoreDrill.RunAsync(_fixture.ConnectionString, dir, "flowledger_drill_old");

        drill.Ok.Should().BeTrue(string.Join("; ", drill.Problems));
    }
}
