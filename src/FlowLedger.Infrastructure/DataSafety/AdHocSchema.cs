using System.Reflection;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Single registry of tables created outside EF migrations (CREATE TABLE IF NOT EXISTS, owned by the Npgsql stores).
/// Restore/import/drill create all of them BEFORE loading a snapshot, so a snapshot that contains one of these tables
/// can always be restored. Each store exposes a public const <c>CreateSqlText</c>; add new stores to <see cref="Scripts"/>
/// (a test fails if a store with CreateSqlText is missing here).
/// </summary>
public static class AdHocSchema
{
    public const string SystemSettingsSql =
        "CREATE TABLE IF NOT EXISTS system_settings (key text PRIMARY KEY, value text NOT NULL, updated_utc timestamptz NOT NULL DEFAULT now());";

    public static IReadOnlyList<string> Scripts { get; } =
    [
        SystemSettingsSql,
        NpgsqlProviderCallLedger.CreateSqlText,
        BackupRunStore.CreateSqlText,
        NpgsqlCategoryRuleStore.CreateSqlText,
        NpgsqlMerchantAliasStore.CreateSqlText,
        NpgsqlUserDirectory.CreateSqlText,
        NpgsqlAccountConnectorLinkStore.CreateSqlText,
        NpgsqlProviderAccountCatalog.CreateSqlText,
        NpgsqlHomeAssistantStore.CreateSqlText,
        NpgsqlIntegrationStore.CreateSqlText,
        FlowLedger.Infrastructure.Persistence.Repositories.DuplicateStore.CreateSqlText,
    ];

    public static string AllSql => string.Join("\n", Scripts);

    /// <summary>Every <c>CreateSqlText</c> constant declared in this assembly (used by the drift guard test).</summary>
    public static IReadOnlyList<string> DiscoverDeclared() =>
        typeof(AdHocSchema).Assembly.GetTypes()
            .Select(t => t.GetField("CreateSqlText", BindingFlags.Public | BindingFlags.Static))
            .Where(f => f is { IsLiteral: true })
            .Select(f => (string)f!.GetRawConstantValue()!)
            .ToList();
}
