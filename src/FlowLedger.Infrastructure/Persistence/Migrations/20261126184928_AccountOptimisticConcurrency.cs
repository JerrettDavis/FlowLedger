using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowLedger.Infrastructure.Persistence.Migrations;

/// <summary>
/// Maps accounts.RowVersion onto Postgres' built-in <c>xmin</c> system column as the optimistic-concurrency token (#157).
/// xmin exists on every row already, so there is nothing to alter; the scaffolded AddColumn would fail
/// ("column name xmin conflicts with a system column"). This migration only records the model snapshot change.
/// </summary>
internal partial class AccountOptimisticConcurrency : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
