using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class MoveSuspiciousDismissalsToEf : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // #87: this table used to be created at runtime by NpgsqlSuspiciousDismissalStore, so live databases already have it.
        migrationBuilder.Sql(
            "CREATE TABLE IF NOT EXISTS suspicious_dismissals (" +
            "tenant_id uuid NOT NULL, transaction_id uuid NOT NULL, note text NULL, " +
            "dismissed_utc timestamp with time zone NOT NULL DEFAULT (now()), " +
            "CONSTRAINT \"PK_suspicious_dismissals\" PRIMARY KEY (tenant_id, transaction_id));");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "suspicious_dismissals");
    }
}
