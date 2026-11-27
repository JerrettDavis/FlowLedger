using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FlowLedger.Infrastructure.HostStores;

/// <summary>One tenant-owned table: where it lives and which column holds the owning tenant.</summary>
internal sealed record TenantTable(string Schema, string Table, string TenantColumn, string EntityName);

/// <summary>
/// The single list of tenant-owned tables, derived from the EF model (#169). Shared by erasure and per-tenant export so a new tenant-owned
/// entity is picked up automatically; a reflection test proves every tenant-scoped entity type is covered. Names come only from the model
/// (never from request input) and are re-validated and quoted before they reach SQL.
/// </summary>
internal static partial class TenantDataCatalog
{
    [GeneratedRegex("^[a-z_][a-z0-9_]*$")]
    private static partial Regex SafeIdentifier();

    /// <summary>Every non-owned root entity that carries a tenant column, one entry per table. Owned collections live in tables that
    /// cascade from their owner. Order with <see cref="OrderChildrenFirst"/> before deleting.</summary>
    public static IReadOnlyList<TenantTable> FromModel(IModel model) =>
        model.GetEntityTypes()
            .Where(e => !e.IsOwned() && e.BaseType is null && e.FindProperty("TenantId") is not null && e.GetTableName() is not null)
            .GroupBy(e => (e.GetSchema() ?? "public", e.GetTableName()!))
            .Select(g => g.First())
            .Select(e =>
            {
                var table = e.GetTableName()!;
                var schema = e.GetSchema() ?? "public";
                var column = e.FindProperty("TenantId")!.GetColumnName(StoreObjectIdentifier.Table(table, e.GetSchema())) ?? "tenant_id";
                return new TenantTable(Validate(schema), Validate(table), Validate(column), e.ClrType.Name);
            })
            .OrderBy(t => t.Table, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Orders table names so a table comes before every table it references (delete children first). The edges are the database's own
    /// foreign keys (some, like the composite tenant keys, are created by migration SQL and are not in the EF model).
    /// </summary>
    public static IReadOnlyList<string> OrderChildrenFirst(IEnumerable<string> tables, IEnumerable<(string Child, string Parent)> edges)
    {
        var remaining = tables.Distinct(StringComparer.Ordinal).ToList();
        var fks = edges.Where(e => e.Child != e.Parent).ToList();
        var ordered = new List<string>();
        while (remaining.Count > 0)
        {
            var next = remaining.Where(t => !fks.Any(f => f.Parent == t && remaining.Contains(f.Child))).ToList();
            if (next.Count == 0)
            {
                next = [remaining[0]]; // a reference cycle: break it arbitrarily rather than loop forever
            }

            ordered.AddRange(next);
            remaining.RemoveAll(next.Contains);
        }

        return ordered;
    }

    /// <summary>Throws unless the name is a plain lower-case SQL identifier (defence in depth: names are model-derived, not user input).</summary>
    public static string Validate(string identifier) =>
        SafeIdentifier().IsMatch(identifier) ? identifier : throw new InvalidOperationException($"Unsafe SQL identifier '{identifier}'.");

    public static string Quote(string identifier) => "\"" + Validate(identifier) + "\"";

    public static string QualifiedName(TenantTable t) => Quote(t.Schema) + "." + Quote(t.Table);
}
