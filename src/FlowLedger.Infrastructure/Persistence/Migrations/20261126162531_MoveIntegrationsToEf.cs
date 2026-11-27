using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
internal partial class MoveIntegrationsToEf : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // #154: created at runtime by NpgsqlIntegrationStore until now, so live databases already have them.
        migrationBuilder.Sql(
            "CREATE TABLE IF NOT EXISTS integrations ( " +
            "tenant_id uuid NOT NULL, id uuid NOT NULL, name text NOT NULL, kind text NOT NULL, " +
            "enabled boolean NOT NULL DEFAULT true, config text NOT NULL, secrets text NULL, " +
            "created_utc timestamp with time zone NOT NULL DEFAULT (now()), " +
            "last_used_utc timestamp with time zone NULL, last_error text NULL, " +
            "CONSTRAINT \"PK_integrations\" PRIMARY KEY (tenant_id, id));");
        migrationBuilder.Sql(
            "CREATE TABLE IF NOT EXISTS integration_deliveries ( " +
            "tenant_id uuid NOT NULL, id uuid NOT NULL, integration_id uuid NOT NULL, " +
            "at_utc timestamp with time zone NOT NULL DEFAULT (now()), " +
            "trigger text NOT NULL, method text NOT NULL, target text NOT NULL, " +
            "status_code integer NULL, duration_ms integer NOT NULL, error text NULL, " +
            "CONSTRAINT \"PK_integration_deliveries\" PRIMARY KEY (tenant_id, id));");
        migrationBuilder.Sql(
            "CREATE INDEX IF NOT EXISTS ix_integration_deliveries_integration " +
            "ON integration_deliveries (tenant_id, integration_id, at_utc DESC);");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "integration_deliveries");

        migrationBuilder.DropTable(
            name: "integrations");
    }
}
