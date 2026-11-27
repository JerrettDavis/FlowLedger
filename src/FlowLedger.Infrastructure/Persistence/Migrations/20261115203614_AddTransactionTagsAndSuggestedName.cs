using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
internal partial class AddTransactionTagsAndSuggestedName : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "suggested_name",
            table: "transactions",
            type: "character varying(300)",
            maxLength: 300,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "tags",
            table: "transactions",
            type: "character varying(500)",
            maxLength: 500,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "suggested_name",
            table: "transactions");

        migrationBuilder.DropColumn(
            name: "tags",
            table: "transactions");
    }
}
