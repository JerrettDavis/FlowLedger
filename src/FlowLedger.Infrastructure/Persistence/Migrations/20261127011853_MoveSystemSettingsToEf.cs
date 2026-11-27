using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MoveSystemSettingsToEf : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #154: created at runtime by the data-environment guard until now, so live databases already have it (and the marker row); IF NOT EXISTS keeps this idempotent.
            migrationBuilder.Sql(
                "CREATE TABLE IF NOT EXISTS system_settings (\n" +
                "    key text NOT NULL,\n" +
                "    value text NOT NULL,\n" +
                "    updated_utc timestamp with time zone NOT NULL DEFAULT (now()),\n" +
                "    CONSTRAINT \"PK_system_settings\" PRIMARY KEY (key));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "system_settings");
        }
    }
}
