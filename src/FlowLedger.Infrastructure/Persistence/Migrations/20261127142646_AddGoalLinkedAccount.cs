using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <summary>
/// #70: an optional account a savings goal is tracked against. The column is nullable (every existing goal stays unlinked) and the
/// migration is idempotent (<c>IF NOT EXISTS</c> / guarded constraint), so re-running it is a no-op. A DB-only composite
/// (tenant_id, linked_account_id) foreign key, the same pattern as <c>assets.linked_account_id</c> (#146), keeps the link inside
/// the tenant; deactivated accounts are never deleted, and a hard-deleted one nulls the link only (ON DELETE SET NULL (col), PostgreSQL 15+).
/// The constraint is added NOT VALID and validated only when no row is orphaned, so legacy data can never fail the migration.
/// </summary>
internal partial class AddGoalLinkedAccount : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE savings_goals ADD COLUMN IF NOT EXISTS linked_account_id uuid NULL;");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IF NOT EXISTS ux_accounts_tenant_id ON accounts (tenant_id, id);");
        migrationBuilder.Sql("""
            DO $fk$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'savings_goals'::regclass AND conname = 'fk_savings_goals_linked_account_tenant') THEN
                    ALTER TABLE savings_goals ADD CONSTRAINT fk_savings_goals_linked_account_tenant
                        FOREIGN KEY (tenant_id, linked_account_id) REFERENCES accounts (tenant_id, id)
                        ON DELETE SET NULL (linked_account_id) DEFERRABLE INITIALLY DEFERRED NOT VALID;
                END IF;
                IF EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'savings_goals'::regclass AND conname = 'fk_savings_goals_linked_account_tenant' AND NOT convalidated) THEN
                    -- A new column has no values, so this validates at once; the guard keeps a re-run safe.
                    ALTER TABLE savings_goals VALIDATE CONSTRAINT fk_savings_goals_linked_account_tenant;
                END IF;
            END
            $fk$;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE savings_goals DROP CONSTRAINT IF EXISTS fk_savings_goals_linked_account_tenant;");
        migrationBuilder.Sql("ALTER TABLE savings_goals DROP COLUMN IF EXISTS linked_account_id;");
    }
}
