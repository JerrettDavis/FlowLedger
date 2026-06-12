using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    account_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    balance_amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    balance_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    credit_limit_amount = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    credit_limit_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    institution = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    external_account_ref = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_balance_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_categories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "recurring_flows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    amount_model = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    recurrence_frequency = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    recurrence_day_of_month = table.Column<int>(type: "integer", nullable: true),
                    recurrence_second_day_of_month = table.Column<int>(type: "integer", nullable: true),
                    recurrence_interval_weeks = table.Column<int>(type: "integer", nullable: true),
                    recurrence_anchor_day_of_week = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true),
                    active_start = table.Column<DateOnly>(type: "date", nullable: false),
                    active_end = table.Column<DateOnly>(type: "date", nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    counterparty = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recurring_flows", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    posted_date = table.Column<DateOnly>(type: "date", nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    merchant_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    fingerprint = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    matched_occurrence_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transactions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "planned_flow_occurrences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    planned_amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    planned_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    planned_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount_variance = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    amount_variance_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    date_variance_days = table.Column<int>(type: "integer", nullable: true),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    match_confidence = table.Column<decimal>(type: "numeric(5,4)", nullable: true),
                    matched_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recurring_flow_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planned_flow_occurrences", x => x.id);
                    table.ForeignKey(
                        name: "FK_planned_flow_occurrences_recurring_flows_recurring_flow_id",
                        column: x => x.recurring_flow_id,
                        principalTable: "recurring_flows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "transaction_splits",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transaction_splits", x => x.id);
                    table.ForeignKey(
                        name: "FK_transaction_splits_transactions_transaction_id",
                        column: x => x.transaction_id,
                        principalTable: "transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_accounts_tenant_id",
                table: "accounts",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_categories_tenant_id",
                table: "categories",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_categories_tenant_path",
                table: "categories",
                columns: new[] { "tenant_id", "path" });

            migrationBuilder.CreateIndex(
                name: "IX_planned_flow_occurrences_recurring_flow_id",
                table: "planned_flow_occurrences",
                column: "recurring_flow_id");

            migrationBuilder.CreateIndex(
                name: "ix_planned_occurrences_date",
                table: "planned_flow_occurrences",
                column: "planned_date");

            migrationBuilder.CreateIndex(
                name: "ix_recurring_flows_tenant_account",
                table: "recurring_flows",
                columns: new[] { "tenant_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_recurring_flows_tenant_id",
                table: "recurring_flows",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_transaction_splits_transaction_id",
                table: "transaction_splits",
                column: "transaction_id");

            migrationBuilder.CreateIndex(
                name: "ix_transactions_tenant_account_date",
                table: "transactions",
                columns: new[] { "tenant_id", "account_id", "effective_date" });

            migrationBuilder.CreateIndex(
                name: "ix_transactions_tenant_date",
                table: "transactions",
                columns: new[] { "tenant_id", "effective_date" });

            // Partial unique index for transaction deduplication (PLAN §10.4).
            // Ensures no two transactions for the same tenant share the same fingerprint.
            // NULL fingerprints are excluded (manual entries without a provider transaction ID).
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX uq_transactions_tenant_fingerprint
                ON transactions (tenant_id, fingerprint)
                WHERE fingerprint IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS uq_transactions_tenant_fingerprint;");

            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "planned_flow_occurrences");

            migrationBuilder.DropTable(
                name: "transaction_splits");

            migrationBuilder.DropTable(
                name: "recurring_flows");

            migrationBuilder.DropTable(
                name: "transactions");
        }
    }
}
