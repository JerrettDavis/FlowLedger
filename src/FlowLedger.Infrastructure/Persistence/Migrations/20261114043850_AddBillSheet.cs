using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddBillSheet : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "sheet_bills",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                section = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                sort_order = table.Column<int>(type: "integer", nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                payer = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                is_debt = table.Column<bool>(type: "boolean", nullable: false),
                is_perpetual = table.Column<bool>(type: "boolean", nullable: false),
                is_optional = table.Column<bool>(type: "boolean", nullable: false),
                is_business = table.Column<bool>(type: "boolean", nullable: false),
                auto_pay = table.Column<bool>(type: "boolean", nullable: false),
                due_day = table.Column<int>(type: "integer", nullable: true),
                estimated = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                actual = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                service = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                account_ref = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                match_text = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_sheet_bills", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "sheet_payments",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                bill_id = table.Column<Guid>(type: "uuid", nullable: false),
                month = table.Column<DateOnly>(type: "date", nullable: false),
                is_paid = table.Column<bool>(type: "boolean", nullable: false),
                amount = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                paid_on = table.Column<DateOnly>(type: "date", nullable: true),
                transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                source = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_sheet_payments", x => x.id);
                table.ForeignKey(
                    name: "FK_sheet_payments_sheet_bills_bill_id",
                    column: x => x.bill_id,
                    principalTable: "sheet_bills",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_sheet_bills_tenant_id",
            table: "sheet_bills",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "ix_sheet_payments_tenant_id",
            table: "sheet_payments",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "ux_sheet_payments_bill_month",
            table: "sheet_payments",
            columns: new[] { "bill_id", "month" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_sheet_payments_transaction",
            table: "sheet_payments",
            columns: new[] { "tenant_id", "transaction_id" },
            unique: true,
            filter: "transaction_id IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "sheet_payments");

        migrationBuilder.DropTable(
            name: "sheet_bills");
    }
}
