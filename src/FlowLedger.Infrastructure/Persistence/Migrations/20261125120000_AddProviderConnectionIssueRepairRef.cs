using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
[DbContext(typeof(FlowLedgerDbContext))]
[Migration("20261125120000_AddProviderConnectionIssueRepairRef")]
public partial class AddProviderConnectionIssueRepairRef : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "issue_provider_code", table: "provider_connections",
            type: "character varying(200)", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<string>(name: "issue_repair_ref", table: "provider_connections",
            type: "character varying(400)", maxLength: 400, nullable: true);
        migrationBuilder.AddColumn<bool>(name: "issue_informational", table: "provider_connections",
            type: "boolean", nullable: false, defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "issue_provider_code", table: "provider_connections");
        migrationBuilder.DropColumn(name: "issue_repair_ref", table: "provider_connections");
        migrationBuilder.DropColumn(name: "issue_informational", table: "provider_connections");
    }
}
