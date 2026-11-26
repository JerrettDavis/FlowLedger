using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(FlowLedgerDbContext))]
    [Migration("20261125000000_AddProviderConnectionIssue")]
    public partial class AddProviderConnectionIssue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "issue_kind", table: "provider_connections",
                type: "character varying(50)", maxLength: 50, nullable: true);
            migrationBuilder.AddColumn<string>(name: "issue_message", table: "provider_connections",
                type: "character varying(500)", maxLength: 500, nullable: true);
            migrationBuilder.AddColumn<bool>(name: "issue_retryable", table: "provider_connections",
                type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<bool>(name: "issue_requires_user_action", table: "provider_connections",
                type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<DateTimeOffset>(name: "issue_retry_after", table: "provider_connections",
                type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<DateTimeOffset>(name: "issue_at", table: "provider_connections",
                type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<int>(name: "consecutive_failures", table: "provider_connections",
                type: "integer", nullable: false, defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var column in new[]
            {
                "issue_kind", "issue_message", "issue_retryable", "issue_requires_user_action",
                "issue_retry_after", "issue_at", "consecutive_failures",
            })
            {
                migrationBuilder.DropColumn(name: column, table: "provider_connections");
            }
        }
    }
}
