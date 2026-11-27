using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddRecurringBillFrequency : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ux_sheet_payments_bill_month",
            table: "sheet_payments");

        migrationBuilder.AddColumn<int>(
            name: "slot",
            table: "sheet_payments",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateOnly>(
            name: "anchor_date",
            table: "sheet_bills",
            type: "date",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "frequency",
            table: "sheet_bills",
            type: "character varying(20)",
            maxLength: 20,
            nullable: false,
            defaultValue: "Monthly");

        migrationBuilder.AddColumn<int>(
            name: "interval_weeks",
            table: "sheet_bills",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "second_due_day",
            table: "sheet_bills",
            type: "integer",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "ux_sheet_payments_bill_month",
            table: "sheet_payments",
            columns: new[] { "bill_id", "month", "slot" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ux_sheet_payments_bill_month",
            table: "sheet_payments");

        migrationBuilder.DropColumn(
            name: "slot",
            table: "sheet_payments");

        migrationBuilder.DropColumn(
            name: "anchor_date",
            table: "sheet_bills");

        migrationBuilder.DropColumn(
            name: "frequency",
            table: "sheet_bills");

        migrationBuilder.DropColumn(
            name: "interval_weeks",
            table: "sheet_bills");

        migrationBuilder.DropColumn(
            name: "second_due_day",
            table: "sheet_bills");

        migrationBuilder.CreateIndex(
            name: "ux_sheet_payments_bill_month",
            table: "sheet_payments",
            columns: new[] { "bill_id", "month" },
            unique: true);
    }
}
