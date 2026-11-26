using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MoveAccountConnectorLinksToEf : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #154: created at runtime by NpgsqlAccountConnectorLinkStore until now, so live databases already have it.
            migrationBuilder.Sql(
                "CREATE TABLE IF NOT EXISTS account_connector_links (" +
                "tenant_id uuid NOT NULL, account_id uuid NOT NULL, connector_id text NOT NULL, provider_account_id text NOT NULL, " +
                "linked_utc timestamp with time zone NOT NULL DEFAULT (now()), " +
                "CONSTRAINT \"PK_account_connector_links\" PRIMARY KEY (tenant_id, account_id));");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_account_connector_links_provider " +
                "ON account_connector_links (tenant_id, connector_id, provider_account_id);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_connector_links");
        }
    }
}
