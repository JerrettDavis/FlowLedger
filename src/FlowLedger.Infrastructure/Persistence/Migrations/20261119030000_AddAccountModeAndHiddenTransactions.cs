using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
[DbContext(typeof(FlowLedgerDbContext))]
[Migration("20261119030000_AddAccountModeAndHiddenTransactions")]
internal partial class AddAccountModeAndHiddenTransactions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "mode",
            table: "accounts",
            type: "character varying(20)",
            maxLength: 20,
            nullable: false,
            defaultValue: "Standard");

        migrationBuilder.AddColumn<bool>(
            name: "hide_transactions",
            table: "accounts",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        // Investment accounts are tracked as holdings by default: their buy/sell lines are noise in everyday views.
        migrationBuilder.Sql("UPDATE accounts SET mode = 'Holding' WHERE account_type = 'Investment';");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "mode", table: "accounts");
        migrationBuilder.DropColumn(name: "hide_transactions", table: "accounts");
    }
}
