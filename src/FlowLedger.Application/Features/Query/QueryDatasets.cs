using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Assets;
using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Features.Transactions;

namespace FlowLedger.Application.Features.Query;

public sealed record DatasetColumnInfo(string Name, string Type, string Description);

public sealed record DatasetInfo(string Name, string Description, IReadOnlyList<DatasetColumnInfo> Columns);

/// <summary>Static description of the datasets the query language exposes (also drives editor autocomplete).</summary>
public static class QueryCatalog
{
    public static IReadOnlyList<DatasetInfo> Datasets { get; } =
    [
        new("transactions",
            "Posted and pending transactions across every account (planned/skipped forecast rows are excluded).",
            [
                new("date", "date", "Posted date, falling back to the effective date"),
                new("amount", "number", "Absolute amount (always positive)"),
                new("signed", "number", "Amount signed by direction: credits positive, debits negative"),
                new("direction", "string", "Credit or Debit"),
                new("description", "string", "Bank description"),
                new("merchant", "string", "Merchant name when known, else the description"),
                new("category", "string", "Category path, or Uncategorized"),
                new("account", "string", "Account name"),
                new("account_type", "string", "Checking, Savings, CreditCard, …"),
                new("status", "string", "Posted or Pending"),
                new("source", "string", "Manual, Import, ProviderSync, …"),
                new("currency", "string", "ISO currency code"),
                new("notes", "string", "Free-text notes"),
                new("is_transfer", "bool", "True when this is one leg of a move between your own accounts (excluded from income/spending)"),
            ]),
        new("accounts",
            "Your accounts with current balances.",
            [
                new("name", "string", "Account name"),
                new("type", "string", "Account type"),
                new("institution", "string", "Institution name"),
                new("balance", "number", "Current balance (negative for debts)"),
                new("currency", "string", "ISO currency code"),
                new("active", "bool", "Whether the account is active"),
            ]),
        new("balance_history",
            "Account balances over time: one row per account per day that a sync captured (the last snapshot of the day).",
            [
                new("date", "date", "Day the balance was captured"),
                new("account", "string", "Account name"),
                new("account_type", "string", "Checking, Savings, CreditCard, ..."),
                new("balance", "number", "Balance at that time (negative for debts)"),
            ]),
        new("assets",
            "Physical and other assets with estimated value.",
            [
                new("name", "string", "Asset name"),
                new("category", "string", "Asset category"),
                new("value", "number", "Current estimated value"),
                new("cost", "number", "Purchase price"),
                new("gain_loss", "number", "Value minus purchase price"),
                new("equity", "number", "Value minus linked debt"),
                new("linked_account", "string", "Linked loan or account"),
                new("purchase_date", "date", "Purchase date"),
                new("valuation_method", "string", "How the value is estimated"),
            ]),
        new("asset_valuations",
            "Every recorded valuation of every asset over time (manual, model, and feed values such as the Redfin Estimate for your home).",
            [
                new("date", "date", "Date the valuation applies to"),
                new("asset", "string", "Asset name"),
                new("category", "string", "Asset category"),
                new("value", "number", "Recorded value on that date"),
                new("source", "string", "Manual, Estimate, realtyapi, appraiser, ..."),
                new("note", "string", "Free-text note"),
            ]),
    ];

    public static IReadOnlyList<string> Functions { get; } =
    [
        "ago(30d)", "today()", "month(date)", "week(date)", "quarter(date)", "year(date)", "startofmonth(date)",
        "startofyear(date)", "dayofweek(date)", "datediff(a, b)", "abs(x)", "round(x, 2)", "floor(x)", "ceil(x)",
        "lower(s)", "upper(s)", "len(s)", "strcat(a, b)", "coalesce(a, b)", "iif(cond, a, b)", "isnull(x)", "tostring(x)",
    ];

    public static IReadOnlyList<string> Aggregates { get; } =
        ["count()", "sum(x)", "avg(x)", "min(x)", "max(x)", "dcount(x)", "countif(cond)", "sumif(x, cond)"];
}

/// <summary>Builds the in-memory, tenant-scoped tables a query runs against.</summary>
public sealed class QueryDatasetProvider(
    ITransactionRepository transactions,
    IAccountRepository accounts,
    ICategoryRepository categories,
    IAssetRepository assets,
    IBalanceHistoryReader balanceHistory)
{
    private const int MaxTransactions = 50_000;

    private Task<IReadOnlyDictionary<string, QueryTable>>? _cached;

    /// <summary>Scoped provider: the (expensive, full-history) tables are built once per request and reused by every query in it.</summary>
    public Task<IReadOnlyDictionary<string, QueryTable>> BuildAsync(CancellationToken ct = default) => _cached ??= BuildCoreAsync(ct);

    private async Task<IReadOnlyDictionary<string, QueryTable>> BuildCoreAsync(CancellationToken ct)
    {
        var accountEntities = await accounts.ListAsync(ct);
        var accountDtos = accountEntities.Select(AccountMapper.ToDto).ToList();
        var accountById = accountDtos.ToDictionary(a => a.Id);
        var categoryById = (await categories.ListAsync(ct)).Select(CategoryMapper.ToDto).ToDictionary(c => c.Id, c => c.Path);

        // MaxTransactions + 1 so hitting the cap is detectable and reported, never silent.
        var loaded = await transactions.ListAllAsync(MaxTransactions + 1, ct);
        var inputTruncated = loaded.Count > MaxTransactions;
        var txRows = new List<object?[]>();
        var loadedDtos = loaded.Take(MaxTransactions).Select(TransactionMapper.ToDto).ToList();
        var transferIds = TransferDetector.Detect(loadedDtos, accountEntities.ToDictionary(a => a.Id, a => a.AccountType.ToString()));
        var excludedAccounts = await AccountVisibility.ExcludedIdsAsync(accounts, null, ct);
        foreach (var t in loadedDtos)
        {
            if (excludedAccounts.Contains(t.AccountId))
            {
                continue;
            }

            if (t.Status is "Planned" or "Skipped")
            {
                continue;
            }

            if (t.Splits is { Count: > 0 } splits)
            {
                // A split transaction contributes one row per part, so spending reports and budgets see each category's share.
                foreach (var s in splits)
                {
                    txRows.Add(TransactionRow(t, accountById, categoryById, transferIds.Contains(t.Id), s.Amount, s.CategoryId, s.Notes ?? t.Notes));
                }

                continue;
            }

            txRows.Add(TransactionRow(t, accountById, categoryById, transferIds.Contains(t.Id)));
        }

        var accountMap = accountEntities.ToDictionary(a => a.Id);
        var today = AssetClock.Today;
        var assetEntities = await assets.ListAsync(ct);
        var valuationRows = assetEntities
            .SelectMany(a => a.Valuations.Select(v => new object?[]
            {
                v.AsOf, a.Name, a.Category.ToString(), v.Amount, v.Source, v.Note,
            }))
            .OrderBy(r => (DateOnly)r[0]!)
            .ToList();
        var assetRows = assetEntities
            .Select(a => AssetMapper.ToDto(a, today, accountMap, withHistory: false))
            .Select(a => new object?[]
            {
                a.Name, a.Category, a.CurrentValue, a.PurchasePrice, a.GainLoss, a.Equity,
                a.LinkedAccountName, a.PurchaseDate, a.ValuationMethod,
            }).ToList();

        var historyRows = (await balanceHistory.DailyAccountBalancesAsync(ct))
            .Where(p => accountById.ContainsKey(p.AccountId))
            .Select(p => new object?[]
            {
                p.Date, accountById[p.AccountId].Name, accountById[p.AccountId].AccountType, p.Balance,
            }).ToList();

        return new Dictionary<string, QueryTable>(StringComparer.OrdinalIgnoreCase)
        {
            ["balance_history"] = Table("balance_history", historyRows),
            ["transactions"] = Table("transactions", txRows, inputTruncated),
            ["accounts"] = Table("accounts", accountDtos.Select(a =>
                new object?[] { a.Name, a.AccountType, a.Institution, a.BalanceAmount, a.BalanceCurrency, a.IsActive }).ToList()),
            ["assets"] = Table("assets", assetRows),
            ["asset_valuations"] = Table("asset_valuations", valuationRows),
        };
    }

    private static QueryTable Table(string name, List<object?[]> rows, bool truncated = false)
        => new(QueryCatalog.Datasets.First(d => d.Name == name).Columns.Select(c => c.Name).ToList(), rows) { InputTruncated = truncated };

    private static object?[] TransactionRow(
        TransactionDto t,
        IReadOnlyDictionary<Guid, AccountDto> accountById,
        IReadOnlyDictionary<Guid, string> categoryById,
        bool isTransfer,
        decimal? amountOverride = null,
        Guid? categoryOverride = null,
        string? notesOverride = null)
    {
        accountById.TryGetValue(t.AccountId, out var account);
        var amount = amountOverride ?? t.Amount;
        var signed = t.Direction == "Debit" ? -amount : amount;
        var categoryKey = amountOverride is null ? t.CategoryId : categoryOverride;
        var category = categoryKey is { } cid && categoryById.TryGetValue(cid, out var path) ? path : "Uncategorized";
        return
        [
            t.PostedDate ?? t.EffectiveDate,
            amount,
            signed,
            t.Direction,
            t.Description,
            string.IsNullOrWhiteSpace(t.MerchantName) ? t.Description : t.MerchantName,
            category,
            account?.Name ?? "Unknown",
            account?.AccountType ?? "Unknown",
            t.Status,
            t.Source,
            t.Currency,
            amountOverride is null ? t.Notes : notesOverride,
            isTransfer,
        ];
    }
}
