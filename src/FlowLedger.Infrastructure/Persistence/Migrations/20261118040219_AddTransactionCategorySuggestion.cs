using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
internal partial class AddTransactionCategorySuggestion : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<double>(
            name: "category_confidence",
            table: "transactions",
            type: "double precision",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "suggested_category_id",
            table: "transactions",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "suggested_confidence",
            table: "transactions",
            type: "double precision",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "category_confidence",
            table: "transactions");

        migrationBuilder.DropColumn(
            name: "suggested_category_id",
            table: "transactions");

        migrationBuilder.DropColumn(
            name: "suggested_confidence",
            table: "transactions");
    }
}
