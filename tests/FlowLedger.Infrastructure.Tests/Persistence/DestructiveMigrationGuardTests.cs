using FlowLedger.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;

namespace FlowLedger.Infrastructure.Tests.Persistence;

public sealed class DestructiveMigrationGuardTests
{
    private sealed class SyntheticDropColumn : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.DropColumn(name: "Notes", table: "Transactions");
    }

    private sealed class SyntheticAdditive : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.AddColumn<string>(name: "Extra", table: "Transactions", nullable: true);
    }

    private static IReadOnlyList<string> Describe(Migration m) => DestructiveMigrationGuard.Describe(m.UpOperations);

    [Fact]
    public void Drop_column_is_destructive() =>
        Describe(new SyntheticDropColumn()).Should().ContainSingle().Which.Should().Contain("DropColumn Transactions.Notes");

    [Fact]
    public void Additive_migration_is_safe() => Describe(new SyntheticAdditive()).Should().BeEmpty();

    [Fact]
    public void Drop_table_delete_data_and_narrowing_are_destructive()
    {
        var ops = new MigrationOperation[]
        {
            new DropTableOperation { Name = "Old" },
            new DeleteDataOperation { Table = "Rows", KeyColumns = ["Id"], KeyValues = new object[,] { { 1 } } },
            new AlterColumnOperation
            {
                Table = "T", Name = "Name", ClrType = typeof(string), MaxLength = 50,
                OldColumn = new AddColumnOperation { ClrType = typeof(string), MaxLength = 200 },
            },
            new AlterColumnOperation
            {
                Table = "T", Name = "Req", ClrType = typeof(string), IsNullable = false,
                OldColumn = new AddColumnOperation { ClrType = typeof(string), IsNullable = true },
            },
        };

        DestructiveMigrationGuard.Describe(ops).Should().HaveCount(4);
    }

    [Fact]
    public void Widening_alter_column_is_safe()
    {
        var ops = new MigrationOperation[]
        {
            new AlterColumnOperation
            {
                Table = "T", Name = "Name", ClrType = typeof(string), MaxLength = 400,
                OldColumn = new AddColumnOperation { ClrType = typeof(string), MaxLength = 200 },
            },
        };

        DestructiveMigrationGuard.Describe(ops).Should().BeEmpty();
    }

    [Fact]
    public void Allow_flag_or_per_migration_confirmation_permits_destructive_migrations()
    {
        IConfiguration Cfg(params (string, string)[] kv) =>
            new ConfigurationBuilder().AddInMemoryCollection(kv.ToDictionary(k => k.Item1, k => (string?)k.Item2)).Build();

        DestructiveMigrationGuard.IsAllowed(Cfg(), "20260101_X").Should().BeFalse("refused by default");
        DestructiveMigrationGuard.IsAllowed(Cfg((DestructiveMigrationGuard.AllowKey, "true")), "20260101_X").Should().BeTrue();
        DestructiveMigrationGuard.IsAllowed(Cfg((DestructiveMigrationGuard.ConfirmedKey, "20260101_X")), "20260101_X").Should().BeTrue();
        DestructiveMigrationGuard.IsAllowed(Cfg((DestructiveMigrationGuard.ConfirmedKey, "20260101_X")), "20260202_Y").Should().BeFalse();
    }
}
