using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
internal partial class UnifyMoneyPrecision : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<decimal>(
            name: "amount",
            table: "transactions",
            type: "numeric(19,4)",
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(18,4)");

        migrationBuilder.AlterColumn<decimal>(
            name: "amount",
            table: "transaction_splits",
            type: "numeric(19,4)",
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(18,4)");

        migrationBuilder.AlterColumn<decimal>(
            name: "amount",
            table: "sheet_payments",
            type: "numeric(19,4)",
            nullable: true,
            oldClrType: typeof(decimal),
            oldType: "numeric(18,4)",
            oldNullable: true);

        migrationBuilder.AlterColumn<decimal>(
            name: "estimated",
            table: "sheet_bills",
            type: "numeric(19,4)",
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(18,4)");

        migrationBuilder.AlterColumn<decimal>(
            name: "actual",
            table: "sheet_bills",
            type: "numeric(19,4)",
            nullable: true,
            oldClrType: typeof(decimal),
            oldType: "numeric(18,4)",
            oldNullable: true);

        migrationBuilder.AlterColumn<decimal>(
            name: "target_amount",
            table: "savings_goals",
            type: "numeric(19,4)",
            precision: 19,
            scale: 4,
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(18,2)",
            oldPrecision: 18,
            oldScale: 2);

        migrationBuilder.AlterColumn<decimal>(
            name: "saved_amount",
            table: "savings_goals",
            type: "numeric(19,4)",
            precision: 19,
            scale: 4,
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(18,2)",
            oldPrecision: 18,
            oldScale: 2);

        migrationBuilder.AlterColumn<decimal>(
            name: "amount",
            table: "recurring_flows",
            type: "numeric(19,4)",
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(18,4)");

        migrationBuilder.AlterColumn<decimal>(
            name: "planned_amount",
            table: "planned_flow_occurrences",
            type: "numeric(19,4)",
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(18,4)");

        migrationBuilder.AlterColumn<decimal>(
            name: "amount_variance",
            table: "planned_flow_occurrences",
            type: "numeric(19,4)",
            nullable: true,
            oldClrType: typeof(decimal),
            oldType: "numeric(18,4)",
            oldNullable: true);

        migrationBuilder.AlterColumn<decimal>(
            name: "balance",
            table: "connector_provider_accounts",
            type: "numeric(19,4)",
            precision: 19,
            scale: 4,
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(18,4)",
            oldPrecision: 18,
            oldScale: 4);

        migrationBuilder.AlterColumn<decimal>(
            name: "monthly_limit",
            table: "budgets",
            type: "numeric(19,4)",
            precision: 19,
            scale: 4,
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(18,2)",
            oldPrecision: 18,
            oldScale: 2);

        migrationBuilder.AlterColumn<decimal>(
            name: "value_amount",
            table: "assets",
            type: "numeric(19,4)",
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(18,4)");

        migrationBuilder.AlterColumn<decimal>(
            name: "salvage_value",
            table: "assets",
            type: "numeric(19,4)",
            nullable: true,
            oldClrType: typeof(decimal),
            oldType: "numeric(18,4)",
            oldNullable: true);

        migrationBuilder.AlterColumn<decimal>(
            name: "purchase_price_amount",
            table: "assets",
            type: "numeric(19,4)",
            nullable: true,
            oldClrType: typeof(decimal),
            oldType: "numeric(18,4)",
            oldNullable: true);

        migrationBuilder.AlterColumn<decimal>(
            name: "amount",
            table: "asset_valuations",
            type: "numeric(19,4)",
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(18,4)");

        migrationBuilder.AlterColumn<decimal>(
            name: "credit_limit_amount",
            table: "accounts",
            type: "numeric(19,4)",
            nullable: true,
            oldClrType: typeof(decimal),
            oldType: "numeric(18,4)",
            oldNullable: true);

        migrationBuilder.AlterColumn<decimal>(
            name: "balance_amount",
            table: "accounts",
            type: "numeric(19,4)",
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(18,4)");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<decimal>(
            name: "amount",
            table: "transactions",
            type: "numeric(18,4)",
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(19,4)");

        migrationBuilder.AlterColumn<decimal>(
            name: "amount",
            table: "transaction_splits",
            type: "numeric(18,4)",
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(19,4)");

        migrationBuilder.AlterColumn<decimal>(
            name: "amount",
            table: "sheet_payments",
            type: "numeric(18,4)",
            nullable: true,
            oldClrType: typeof(decimal),
            oldType: "numeric(19,4)",
            oldNullable: true);

        migrationBuilder.AlterColumn<decimal>(
            name: "estimated",
            table: "sheet_bills",
            type: "numeric(18,4)",
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(19,4)");

        migrationBuilder.AlterColumn<decimal>(
            name: "actual",
            table: "sheet_bills",
            type: "numeric(18,4)",
            nullable: true,
            oldClrType: typeof(decimal),
            oldType: "numeric(19,4)",
            oldNullable: true);

        migrationBuilder.AlterColumn<decimal>(
            name: "target_amount",
            table: "savings_goals",
            type: "numeric(18,2)",
            precision: 18,
            scale: 2,
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(19,4)",
            oldPrecision: 19,
            oldScale: 4);

        migrationBuilder.AlterColumn<decimal>(
            name: "saved_amount",
            table: "savings_goals",
            type: "numeric(18,2)",
            precision: 18,
            scale: 2,
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(19,4)",
            oldPrecision: 19,
            oldScale: 4);

        migrationBuilder.AlterColumn<decimal>(
            name: "amount",
            table: "recurring_flows",
            type: "numeric(18,4)",
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(19,4)");

        migrationBuilder.AlterColumn<decimal>(
            name: "planned_amount",
            table: "planned_flow_occurrences",
            type: "numeric(18,4)",
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(19,4)");

        migrationBuilder.AlterColumn<decimal>(
            name: "amount_variance",
            table: "planned_flow_occurrences",
            type: "numeric(18,4)",
            nullable: true,
            oldClrType: typeof(decimal),
            oldType: "numeric(19,4)",
            oldNullable: true);

        migrationBuilder.AlterColumn<decimal>(
            name: "balance",
            table: "connector_provider_accounts",
            type: "numeric(18,4)",
            precision: 18,
            scale: 4,
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(19,4)",
            oldPrecision: 19,
            oldScale: 4);

        migrationBuilder.AlterColumn<decimal>(
            name: "monthly_limit",
            table: "budgets",
            type: "numeric(18,2)",
            precision: 18,
            scale: 2,
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(19,4)",
            oldPrecision: 19,
            oldScale: 4);

        migrationBuilder.AlterColumn<decimal>(
            name: "value_amount",
            table: "assets",
            type: "numeric(18,4)",
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(19,4)");

        migrationBuilder.AlterColumn<decimal>(
            name: "salvage_value",
            table: "assets",
            type: "numeric(18,4)",
            nullable: true,
            oldClrType: typeof(decimal),
            oldType: "numeric(19,4)",
            oldNullable: true);

        migrationBuilder.AlterColumn<decimal>(
            name: "purchase_price_amount",
            table: "assets",
            type: "numeric(18,4)",
            nullable: true,
            oldClrType: typeof(decimal),
            oldType: "numeric(19,4)",
            oldNullable: true);

        migrationBuilder.AlterColumn<decimal>(
            name: "amount",
            table: "asset_valuations",
            type: "numeric(18,4)",
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(19,4)");

        migrationBuilder.AlterColumn<decimal>(
            name: "credit_limit_amount",
            table: "accounts",
            type: "numeric(18,4)",
            nullable: true,
            oldClrType: typeof(decimal),
            oldType: "numeric(19,4)",
            oldNullable: true);

        migrationBuilder.AlterColumn<decimal>(
            name: "balance_amount",
            table: "accounts",
            type: "numeric(18,4)",
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(19,4)");
    }
}
