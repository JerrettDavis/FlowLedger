using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
[DbContext(typeof(FlowLedgerDbContext))]
[Migration("20261118235900_AddTransactionDateCreatedIndex")]
public partial class AddTransactionDateCreatedIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "ix_transactions_tenant_date_created",
            table: "transactions",
            columns: new[] { "tenant_id", "effective_date", "created_at" },
            descending: new[] { false, true, true });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_transactions_tenant_date_created",
            table: "transactions");
    }
}
