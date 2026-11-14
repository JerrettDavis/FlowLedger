using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DedupeDetectedBills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Racing refreshes created copies of detected rows: keep the oldest per name, cascade removes the cells.
            migrationBuilder.Sql(@"
                DELETE FROM sheet_bills b USING (
                    SELECT id, ROW_NUMBER() OVER (PARTITION BY tenant_id, name ORDER BY created_at, id) AS rn
                    FROM sheet_bills WHERE section = 'Detected') d
                WHERE b.id = d.id AND d.rn > 1;");

            migrationBuilder.CreateIndex(
                name: "ux_sheet_bills_detected_name",
                table: "sheet_bills",
                columns: new[] { "tenant_id", "name" },
                unique: true,
                filter: "section = 'Detected'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_sheet_bills_detected_name",
                table: "sheet_bills");
        }
    }
}
