using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>
/// The destructive-migration guard (#63) runs on every upgrade, so it must not misfire on migrations that already shipped:
/// any pending shipped migration it refuses would block a real upgrade until the operator overrides it.
/// </summary>
public sealed class ShippedMigrationsGuardTests
{
    /// <summary>Migrations known to be destructive on purpose (each was reviewed). New entries need a reason.</summary>
    private static readonly string[] KnownDestructive =
    [
        "20261114050656_DedupeDetectedBills", // deletes duplicate auto-detected bill rows (reviewed; a database that old needs DestructiveMigrations confirmed once)
    ];

    [Fact]
    public void No_shipped_migration_is_flagged_unless_it_is_listed_as_reviewed()
    {
        var options = new DbContextOptionsBuilder<FlowLedgerDbContext>()
            .UseNpgsql("Host=localhost;Database=unused", n => n.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name))
            .Options;
        using var ctx = new FlowLedgerDbContext(options, new NoOpDomainEventDispatcher(), UnscopedTenantContext.Instance);
        var assembly = ctx.GetService<IMigrationsAssembly>();

        var flagged = DestructiveMigrationGuard.Scan(assembly, ctx.Database.ProviderName!, assembly.Migrations.Keys);

        flagged.Keys.Should().BeSubsetOf(KnownDestructive, "a newly flagged migration would be refused on upgrade until confirmed: " + string.Join("; ", flagged.Select(f => $"{f.Key}: {string.Join(", ", f.Value)}")));
    }

    [Fact]
    public void Constraint_and_index_housekeeping_in_raw_sql_is_not_destructive()
    {
        var ops = new MigrationOperation[]
        {
            new SqlOperation { Sql = "ALTER TABLE t DROP CONSTRAINT IF EXISTS fk_x;" },
            new SqlOperation { Sql = "DROP INDEX IF EXISTS ix_t_a;" },
            new SqlOperation { Sql = "ALTER TABLE t ADD CONSTRAINT fk FOREIGN KEY (a) REFERENCES u (id) ON DELETE CASCADE;" },
            new SqlOperation { Sql = "ALTER TABLE t ALTER COLUMN a DROP DEFAULT;" },
        };

        DestructiveMigrationGuard.Describe(ops).Should().BeEmpty();
    }

    [Fact]
    public void Raw_sql_that_drops_tables_or_deletes_rows_is_destructive()
    {
        var ops = new MigrationOperation[]
        {
            new SqlOperation { Sql = "DROP TABLE legacy;" },
            new SqlOperation { Sql = "DELETE FROM transactions WHERE true;" },
            new SqlOperation { Sql = "TRUNCATE TABLE audit_events;" },
            new SqlOperation { Sql = "ALTER TABLE t DROP COLUMN notes;" },
        };

        DestructiveMigrationGuard.Describe(ops).Should().HaveCount(4);
    }
}
