using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MoveDuplicatesToEf : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #154: created at runtime by DuplicateStore until now, so live databases already have them.
            migrationBuilder.Sql(
                "CREATE TABLE IF NOT EXISTS merged_transactions ( " +
                "tenant_id uuid NOT NULL, duplicate_id uuid NOT NULL, survivor_id uuid NOT NULL, fingerprint text NULL, " +
                "merged_utc timestamp with time zone NOT NULL DEFAULT (now()), auto boolean NOT NULL DEFAULT false, " +
                "reason text NOT NULL DEFAULT '', snapshot text NOT NULL DEFAULT '', " +
                "CONSTRAINT \"PK_merged_transactions\" PRIMARY KEY (tenant_id, duplicate_id));");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_merged_transactions_tenant_fingerprint ON merged_transactions (tenant_id, fingerprint);");
            migrationBuilder.Sql(
                "CREATE TABLE IF NOT EXISTS duplicate_dismissals ( " +
                "tenant_id uuid NOT NULL, pair_key text NOT NULL, " +
                "dismissed_utc timestamp with time zone NOT NULL DEFAULT (now()), " +
                "CONSTRAINT \"PK_duplicate_dismissals\" PRIMARY KEY (tenant_id, pair_key));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "duplicate_dismissals");

            migrationBuilder.DropTable(
                name: "merged_transactions");
        }
    }
}
