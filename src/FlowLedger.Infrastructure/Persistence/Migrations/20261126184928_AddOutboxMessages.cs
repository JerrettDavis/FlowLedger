using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
internal partial class AddOutboxMessages : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Idempotent (#92): safe to re-run against a database that already has the table.
        migrationBuilder.Sql(
            "CREATE TABLE IF NOT EXISTS outbox_messages ( " +
            "id uuid NOT NULL, tenant_id uuid NOT NULL, event_type text NOT NULL, payload text NOT NULL, " +
            "occurred_utc timestamp with time zone NOT NULL, attempts integer NOT NULL DEFAULT 0, " +
            "next_attempt_utc timestamp with time zone NOT NULL, locked_until_utc timestamp with time zone NULL, " +
            "lock_token uuid NULL, completed_handlers text NOT NULL DEFAULT '', " +
            "processed_utc timestamp with time zone NULL, dead_lettered_utc timestamp with time zone NULL, last_error text NULL, " +
            "CONSTRAINT \"PK_outbox_messages\" PRIMARY KEY (id));");
        migrationBuilder.Sql(
            "CREATE INDEX IF NOT EXISTS ix_outbox_messages_pending ON outbox_messages (tenant_id, next_attempt_utc) " +
            "WHERE processed_utc IS NULL AND dead_lettered_utc IS NULL;");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "outbox_messages");
    }
}
