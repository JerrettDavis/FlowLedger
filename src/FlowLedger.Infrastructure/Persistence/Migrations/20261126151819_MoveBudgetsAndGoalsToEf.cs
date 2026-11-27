using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
internal partial class MoveBudgetsAndGoalsToEf : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // #154: created at runtime by NpgsqlBudgetStore / NpgsqlGoalStore until now, so live databases already have them.
        // The CHECK constraints are part of the original definitions and are kept.
        migrationBuilder.Sql(
            "CREATE TABLE IF NOT EXISTS budgets (" +
            "tenant_id uuid NOT NULL, category text NOT NULL, " +
            "monthly_limit numeric(18,2) NOT NULL CHECK (monthly_limit >= 0), " +
            "updated_utc timestamp with time zone NOT NULL DEFAULT (now()), " +
            "CONSTRAINT \"PK_budgets\" PRIMARY KEY (tenant_id, category));");
        migrationBuilder.Sql(
            "CREATE TABLE IF NOT EXISTS savings_goals (" +
            "tenant_id uuid NOT NULL, id uuid NOT NULL, name text NOT NULL, " +
            "target_amount numeric(18,2) NOT NULL CHECK (target_amount > 0), " +
            "saved_amount numeric(18,2) NOT NULL DEFAULT 0 CHECK (saved_amount >= 0), " +
            "target_date date NULL, " +
            "updated_utc timestamp with time zone NOT NULL DEFAULT (now()), " +
            "CONSTRAINT \"PK_savings_goals\" PRIMARY KEY (tenant_id, id));");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "budgets");

        migrationBuilder.DropTable(
            name: "savings_goals");
    }
}
