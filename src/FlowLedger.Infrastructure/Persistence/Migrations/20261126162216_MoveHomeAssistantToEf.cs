using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
internal partial class MoveHomeAssistantToEf : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // #154: created at runtime by NpgsqlHomeAssistantStore until now, so live databases already have them.
        migrationBuilder.Sql(
            "CREATE TABLE IF NOT EXISTS home_assistant_settings ( " +
            "tenant_id uuid NOT NULL, enabled boolean NOT NULL DEFAULT false, webhook text NULL, " +
            "share_amounts boolean NOT NULL DEFAULT false, include_names boolean NOT NULL DEFAULT true, " +
            "lead_days integer NOT NULL DEFAULT 3, kinds text NOT NULL DEFAULT '', " +
            "last_sent_utc timestamp with time zone NULL, last_error text NULL, " +
            "updated_utc timestamp with time zone NOT NULL DEFAULT (now()), " +
            "CONSTRAINT \"PK_home_assistant_settings\" PRIMARY KEY (tenant_id));");
        migrationBuilder.Sql(
            "CREATE TABLE IF NOT EXISTS home_assistant_sent ( " +
            "tenant_id uuid NOT NULL, dedupe_key text NOT NULL, " +
            "sent_utc timestamp with time zone NOT NULL DEFAULT (now()), " +
            "CONSTRAINT \"PK_home_assistant_sent\" PRIMARY KEY (tenant_id, dedupe_key));");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "home_assistant_sent");

        migrationBuilder.DropTable(
            name: "home_assistant_settings");
    }
}
