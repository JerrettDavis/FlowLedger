using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
internal partial class MoveProviderAccountCatalogToEf : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // #154: created at runtime by NpgsqlProviderAccountCatalog until now, so live databases already have it.
        migrationBuilder.Sql(
            "CREATE TABLE IF NOT EXISTS connector_provider_accounts ( " +
            "tenant_id uuid NOT NULL, connector_id text NOT NULL, provider_account_id text NOT NULL, " +
            "name text NOT NULL, account_type text NOT NULL, balance numeric(18,4) NOT NULL, currency text NOT NULL, " +
            "last_seen_utc timestamp with time zone NOT NULL DEFAULT (now()), " +
            "CONSTRAINT \"PK_connector_provider_accounts\" PRIMARY KEY (tenant_id, connector_id, provider_account_id));");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "connector_provider_accounts");
    }
}
