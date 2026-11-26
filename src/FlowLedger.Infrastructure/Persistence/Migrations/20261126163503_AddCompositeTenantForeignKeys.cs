using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// #146 (DB-level defense in depth): composite (tenant_id, id) foreign keys so a row can only reference an account or category
    /// that belongs to the SAME tenant, whatever the application layer does. Application-level reference validation stays; this
    /// makes a cross-tenant or dangling reference impossible to persist.
    ///
    /// Safe on live data and idempotent:
    ///  - each parent gets a unique index on (tenant_id, id) (CREATE UNIQUE INDEX IF NOT EXISTS) that the composite FK targets;
    ///  - each FK is DEFERRABLE INITIALLY DEFERRED: it is checked at commit, because EF does not know about these DB-only keys and
    ///    inserts an account and its transactions/snapshots in one SaveChanges in an arbitrary order;
    ///  - each FK is added NOT VALID (enforced for every new write immediately, no full-table lock to scan) only when missing;
    ///  - existing rows are then checked: with no orphans the constraint is VALIDATEd; with orphans a WARNING reports the count and
    ///    the constraint stays NOT VALID (still enforcing new writes) so nothing is deleted or rewritten automatically and the
    ///    migration never fails on legacy data. Re-running the migration (or its SQL) validates once the orphans are cleaned up;
    ///  - deleting a category nulls the referencing column only (ON DELETE SET NULL (col), PostgreSQL 15+), matching how the app
    ///    already treats a missing category; accounts are only deactivated (soft delete, DeactivateAccountHandler), never removed, so account references use NO ACTION.
    ///
    /// The constraints are deliberately DB-only (not in the EF model), so the model snapshot is unchanged.
    /// </summary>
    public partial class AddCompositeTenantForeignKeys : Migration
    {
        private sealed record Fk(string Name, string Child, string Column, string Parent, string OnDelete = null);

        private static readonly string[] ParentTables = ["accounts", "categories"];

        private static readonly Fk[] Keys =
        [
            new("fk_transactions_account_tenant", "transactions", "account_id", "accounts"),
            new("fk_transactions_category_tenant", "transactions", "category_id", "categories", "SET NULL (category_id)"),
            new("fk_transactions_suggested_category_tenant", "transactions", "suggested_category_id", "categories", "SET NULL (suggested_category_id)"),
            new("fk_recurring_flows_account_tenant", "recurring_flows", "account_id", "accounts"),
            new("fk_recurring_flows_category_tenant", "recurring_flows", "category_id", "categories", "SET NULL (category_id)"),
            new("fk_categories_parent_tenant", "categories", "parent_id", "categories", "SET NULL (parent_id)"),
            new("fk_assets_linked_account_tenant", "assets", "linked_account_id", "accounts", "SET NULL (linked_account_id)"),
            new("fk_account_balance_snapshots_account_tenant", "account_balance_snapshots", "account_id", "accounts"),
            new("fk_account_connector_links_account_tenant", "account_connector_links", "account_id", "accounts"),
            new("fk_account_rates_account_tenant", "account_rates", "account_id", "accounts"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var parent in ParentTables)
            {
                migrationBuilder.Sql($"CREATE UNIQUE INDEX IF NOT EXISTS ux_{parent}_tenant_id ON {parent} (tenant_id, id);");
            }

            foreach (var fk in Keys)
            {
                migrationBuilder.Sql(AddAndValidate(fk));
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var fk in Keys.Reverse())
            {
                migrationBuilder.Sql($"ALTER TABLE {fk.Child} DROP CONSTRAINT IF EXISTS {fk.Name};");
            }

            foreach (var parent in ParentTables)
            {
                migrationBuilder.Sql($"DROP INDEX IF EXISTS ux_{parent}_tenant_id;");
            }
        }

        private static string AddAndValidate(Fk fk)
        {
            var onDelete = fk.OnDelete is null ? string.Empty : $" ON DELETE {fk.OnDelete}";
            return $$"""
                DO $fk$
                DECLARE orphans bigint;
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = '{{fk.Child}}'::regclass AND conname = '{{fk.Name}}') THEN
                        ALTER TABLE {{fk.Child}} ADD CONSTRAINT {{fk.Name}}
                            FOREIGN KEY (tenant_id, {{fk.Column}}) REFERENCES {{fk.Parent}} (tenant_id, id){{onDelete}} DEFERRABLE INITIALLY DEFERRED NOT VALID;
                    END IF;
                    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = '{{fk.Child}}'::regclass AND conname = '{{fk.Name}}' AND NOT convalidated) THEN
                        SELECT count(*) INTO orphans FROM {{fk.Child}} c
                            WHERE c.{{fk.Column}} IS NOT NULL
                              AND NOT EXISTS (SELECT 1 FROM {{fk.Parent}} p WHERE p.tenant_id = c.tenant_id AND p.id = c.{{fk.Column}});
                        IF orphans = 0 THEN
                            ALTER TABLE {{fk.Child}} VALIDATE CONSTRAINT {{fk.Name}};
                        ELSE
                            RAISE WARNING '#146: % row(s) in {{fk.Child}} reference a missing or foreign-tenant {{fk.Parent}} row via {{fk.Column}}; {{fk.Name}} stays NOT VALID (enforced for new writes) until they are cleaned up.', orphans;
                        END IF;
                    END IF;
                END
                $fk$;
                """;
        }
    }
}
