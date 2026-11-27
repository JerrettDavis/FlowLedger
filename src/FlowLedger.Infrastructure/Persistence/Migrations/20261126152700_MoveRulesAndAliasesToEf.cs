using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
internal partial class MoveRulesAndAliasesToEf : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // #154: created at runtime by the Npgsql rule/alias stores until now, so live databases already have them.
        migrationBuilder.Sql(
            "CREATE TABLE IF NOT EXISTS category_rules (" +
            "tenant_id uuid NOT NULL, pattern text NOT NULL, category_path text NOT NULL, " +
            "updated_utc timestamp with time zone NOT NULL DEFAULT (now()), " +
            "CONSTRAINT \"PK_category_rules\" PRIMARY KEY (tenant_id, pattern));");
        migrationBuilder.Sql(
            "CREATE TABLE IF NOT EXISTS merchant_aliases (" +
            "tenant_id uuid NOT NULL, pattern text NOT NULL, alias text NOT NULL, " +
            "updated_utc timestamp with time zone NOT NULL DEFAULT (now()), " +
            "CONSTRAINT \"PK_merchant_aliases\" PRIMARY KEY (tenant_id, pattern));");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "category_rules");

        migrationBuilder.DropTable(
            name: "merchant_aliases");
    }
}
