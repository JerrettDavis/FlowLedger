using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <summary>
/// Adds sync_cursors.last_successful_sync_at (#120): the throttle clock is the last SUCCESSFUL sync, not the last cursor write
/// (which also advances mid-way through a sync that later fails). Idempotent, and existing rows are seeded from last_synced_at
/// so an upgrade does not suddenly un-throttle every tenant.
/// </summary>
[DbContext(typeof(FlowLedgerDbContext))]
[Migration("20261127000000_AddSyncCursorLastSuccessfulSyncAt")]
public partial class AddSyncCursorLastSuccessfulSyncAt : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "ALTER TABLE sync_cursors ADD COLUMN IF NOT EXISTS last_successful_sync_at timestamp with time zone NULL;");
        migrationBuilder.Sql(
            "UPDATE sync_cursors SET last_successful_sync_at = last_synced_at WHERE last_successful_sync_at IS NULL;");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE sync_cursors DROP COLUMN IF EXISTS last_successful_sync_at;");
    }
}
