using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
internal partial class AddBudgetRollover : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "carried_amount",
            table: "budgets",
            type: "numeric(19,4)",
            precision: 19,
            scale: 4,
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.AddColumn<string>(
            name: "carried_for_month",
            table: "budgets",
            type: "character varying(7)",
            maxLength: 7,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "rollover",
            table: "budgets",
            type: "boolean",
            nullable: false,
            defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "carried_amount",
            table: "budgets");

        migrationBuilder.DropColumn(
            name: "carried_for_month",
            table: "budgets");

        migrationBuilder.DropColumn(
            name: "rollover",
            table: "budgets");
    }
}
