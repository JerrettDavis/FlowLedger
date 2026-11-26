using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MoveRecurringDismissalsToEf : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #87: this table used to be created at runtime by NpgsqlRecurringDismissalStore, so live databases already have it.
            // IF NOT EXISTS keeps the migration idempotent there and creates it on fresh databases.
            migrationBuilder.Sql(
                "CREATE TABLE IF NOT EXISTS recurring_dismissals (" +
                "tenant_id uuid NOT NULL, suggestion_key text NOT NULL, " +
                "dismissed_utc timestamp with time zone NOT NULL DEFAULT (now()), " +
                "CONSTRAINT \"PK_recurring_dismissals\" PRIMARY KEY (tenant_id, suggestion_key));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recurring_dismissals");
        }
    }
}
