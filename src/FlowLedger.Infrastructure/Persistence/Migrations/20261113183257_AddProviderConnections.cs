using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddProviderConnections : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "provider_connections",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                provider_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                member_provider_id = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                institution = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_provider_connections", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "uq_provider_connections_tenant_provider",
            table: "provider_connections",
            columns: new[] { "tenant_id", "provider_name" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "provider_connections");
    }
}
