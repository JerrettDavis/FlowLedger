using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class MoveUserDirectoryAndTenantsToEf : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // #154: created at runtime by the user directory and registry stores until now, so live databases already have them (app_users may predate email_verified); IF NOT EXISTS / ADD COLUMN IF NOT EXISTS keep this idempotent and the backfill registers every tenant that already has a login.
        migrationBuilder.Sql(
            "CREATE TABLE IF NOT EXISTS tenants (\n" +
            "    id uuid NOT NULL,\n" +
            "    status text NOT NULL DEFAULT 'active',\n" +
            "    created_utc timestamp with time zone NOT NULL DEFAULT (now()),\n" +
            "    orphaned_utc timestamp with time zone NULL,\n" +
            "    deleted_at timestamp with time zone NULL,\n" +
            "    CONSTRAINT \"PK_tenants\" PRIMARY KEY (id));");
        migrationBuilder.Sql(
            "CREATE TABLE IF NOT EXISTS app_users (\n" +
            "    id uuid NOT NULL,\n" +
            "    issuer text NOT NULL,\n" +
            "    subject text NOT NULL,\n" +
            "    email text NULL,\n" +
            "    display_name text NULL,\n" +
            "    tenant_id uuid NOT NULL,\n" +
            "    created_utc timestamp with time zone NOT NULL DEFAULT (now()),\n" +
            "    last_login_utc timestamp with time zone NOT NULL DEFAULT (now()),\n" +
            "    email_verified boolean NOT NULL DEFAULT FALSE,\n" +
            "    CONSTRAINT \"PK_app_users\" PRIMARY KEY (id),\n" +
            "    CONSTRAINT app_users_issuer_subject_key UNIQUE (issuer, subject));");
        migrationBuilder.Sql(
            "ALTER TABLE app_users ADD COLUMN IF NOT EXISTS email_verified boolean NOT NULL DEFAULT FALSE;");
        migrationBuilder.Sql(
            "CREATE INDEX IF NOT EXISTS ix_app_users_tenant ON app_users (tenant_id);");
        migrationBuilder.Sql(
            "CREATE INDEX IF NOT EXISTS ix_app_users_email ON app_users (lower(email));");
        migrationBuilder.Sql(
            "CREATE TABLE IF NOT EXISTS app_user_link_codes (\n" +
            "    code_hash text NOT NULL,\n" +
            "    tenant_id uuid NOT NULL,\n" +
            "    created_utc timestamp with time zone NOT NULL DEFAULT (now()),\n" +
            "    expires_utc timestamp with time zone NOT NULL,\n" +
            "    used_utc timestamp with time zone NULL,\n" +
            "    CONSTRAINT \"PK_app_user_link_codes\" PRIMARY KEY (code_hash));");
        migrationBuilder.Sql(
            "INSERT INTO tenants (id) SELECT DISTINCT tenant_id FROM app_users ON CONFLICT (id) DO NOTHING;");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "app_user_link_codes");

        migrationBuilder.DropTable(
            name: "app_users");

        migrationBuilder.DropTable(
            name: "tenants");
    }
}
