using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;

namespace FlowLedger.Infrastructure.Persistence;

/// <summary>
/// Scans the Up operations of pending migrations for data-destroying changes (DropTable / DropColumn / narrowing or
/// type-changing AlterColumn / DeleteData / destructive raw SQL) so startup can refuse them unless an operator
/// explicitly allowed it.
/// </summary>
internal static partial class DestructiveMigrationGuard
{
    /// <summary>Set to true to allow any destructive pending migration.</summary>
    public const string AllowKey = "Database:AllowDestructiveMigrations";

    /// <summary>Comma-separated migration ids (or '*') the operator has individually confirmed.</summary>
    public const string ConfirmedKey = "Database:ConfirmedDestructiveMigrations";

    public static bool IsAllowed(IConfiguration config, string migrationId)
    {
        if (bool.TryParse(config[AllowKey], out var allow) && allow)
        {
            return true;
        }

        var confirmed = (config[ConfirmedKey] ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return confirmed.Any(c => c == "*" || string.Equals(c, migrationId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns a human-readable description of each destructive operation; empty when the migration is safe.</summary>
    public static IReadOnlyList<string> Describe(IEnumerable<MigrationOperation> operations)
    {
        var findings = new List<string>();
        foreach (var op in operations)
        {
            switch (op)
            {
                case DropTableOperation t:
                    findings.Add($"DropTable {t.Name}");
                    break;
                case DropColumnOperation c:
                    findings.Add($"DropColumn {c.Table}.{c.Name}");
                    break;
                case DropSchemaOperation s:
                    findings.Add($"DropSchema {s.Name}");
                    break;
                case DeleteDataOperation d:
                    findings.Add($"DeleteData {d.Table}");
                    break;
                case AlterColumnOperation a when IsNarrowing(a):
                    findings.Add($"AlterColumn (narrowing) {a.Table}.{a.Name}");
                    break;
                case SqlOperation sql when DestructiveSql().IsMatch(sql.Sql):
                    findings.Add("Raw SQL containing DROP/DELETE/TRUNCATE");
                    break;
            }
        }

        return findings;
    }

    /// <summary>Instantiates each pending migration and returns the destructive findings keyed by migration id.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Scan(IMigrationsAssembly assembly, string activeProvider, IEnumerable<string> pendingIds)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var id in pendingIds)
        {
            if (!assembly.Migrations.TryGetValue(id, out var type))
            {
                continue;
            }

            var findings = Describe(assembly.CreateMigration(type, activeProvider).UpOperations);
            if (findings.Count > 0)
            {
                result[id] = findings;
            }
        }

        return result;
    }

    private static bool IsNarrowing(AlterColumnOperation a)
    {
        var old = a.OldColumn;
        if (a.ClrType != old.ClrType)
        {
            return true;
        }

        if (old.IsNullable && !a.IsNullable)
        {
            return true;
        }

        if (old.MaxLength is { } oldMax && a.MaxLength is { } newMax && newMax < oldMax)
        {
            return true;
        }

        return old.ColumnType is not null && a.ColumnType is not null
            && !string.Equals(old.ColumnType, a.ColumnType, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\b(DROP|DELETE|TRUNCATE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DestructiveSql();
}
