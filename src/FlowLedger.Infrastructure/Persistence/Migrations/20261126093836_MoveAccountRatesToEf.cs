using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class MoveAccountRatesToEf : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // #87: created at runtime by NpgsqlAccountRateStore until now, so live databases already have it. The CHECK constraints
        // are part of the original definition and are kept.
        migrationBuilder.Sql(
            "CREATE TABLE IF NOT EXISTS account_rates (" +
            "tenant_id uuid NOT NULL, account_id uuid NOT NULL, " +
            "rate_percent numeric(7,3) NOT NULL CHECK (rate_percent >= 0 AND rate_percent <= 100), " +
            "remaining_months int NULL CHECK (remaining_months IS NULL OR (remaining_months >= 1 AND remaining_months <= 600)), " +
            "updated_utc timestamp with time zone NOT NULL DEFAULT (now()), " +
            "CONSTRAINT \"PK_account_rates\" PRIMARY KEY (tenant_id, account_id));");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "account_rates");
    }
}
