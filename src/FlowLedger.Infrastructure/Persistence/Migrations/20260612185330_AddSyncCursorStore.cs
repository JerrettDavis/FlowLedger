using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncCursorStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sync_cursors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    provider_account_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    cursor_value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    last_synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_cursors", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sync_cursors_tenant_id",
                table: "sync_cursors",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "uq_sync_cursors_tenant_provider_account",
                table: "sync_cursors",
                columns: new[] { "tenant_id", "provider_name", "provider_account_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sync_cursors");
        }
    }
}
