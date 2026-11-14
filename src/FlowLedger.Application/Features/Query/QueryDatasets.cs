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
    IAssetRepository assets)
{
    private const int PageSize = 2000;
    private const int MaxTransactions = 50_000;

    public async Task<IReadOnlyDictionary<string, QueryTable>> BuildAsync(CancellationToken ct = default)
    {
        var accountEntities = await accounts.ListAsync(ct);
        var accountDtos = accountEntities.Select(AccountMapper.ToDto).ToList();
        var accountById = accountDtos.ToDictionary(a => a.Id);
        var categoryById = (await categories.ListAsync(ct)).Select(CategoryMapper.ToDto).ToDictionary(c => c.Id, c => c.Path);

        var txRows = new List<object?[]>();
        for (var skip = 0; skip < MaxTransactions; skip += PageSize)
        {
            var page = await transactions.ListAsync(null, null, null, skip, PageSize, ct);
            foreach (var t in page.Select(TransactionMapper.ToDto))
            {
                if (t.Status is "Planned" or "Skipped")
                {
                    continue;
                }

                txRows.Add(TransactionRow(t, accountById, categoryById));
            }
            if (page.Count < PageSize)
            {
                break;
            }
        }

        var accountMap = accountEntities.ToDictionary(a => a.Id);
        var today = AssetClock.Today;
        var assetRows = (await assets.ListAsync(ct))
            .Select(a => AssetMapper.ToDto(a, today, accountMap, withHistory: false))
            .Select(a => new object?[]
            {
                a.Name, a.Category, a.CurrentValue, a.PurchasePrice, a.GainLoss, a.Equity,
                a.LinkedAccountName, a.PurchaseDate, a.ValuationMethod,
            }).ToList();

        return new Dictionary<string, QueryTable>(StringComparer.OrdinalIgnoreCase)
        {
            ["transactions"] = Table("transactions", txRows),
            ["accounts"] = Table("accounts", accountDtos.Select(a =>
                new object?[] { a.Name, a.AccountType, a.Institution, a.BalanceAmount, a.BalanceCurrency, a.IsActive }).ToList()),
            ["assets"] = Table("assets", assetRows),
        };
    }

    private static QueryTable Table(string name, List<object?[]> rows)
        => new(QueryCatalog.Datasets.First(d => d.Name == name).Columns.Select(c => c.Name).ToList(), rows);

    private static object?[] TransactionRow(
        TransactionDto t,
        IReadOnlyDictionary<Guid, AccountDto> accountById,
        IReadOnlyDictionary<Guid, string> categoryById)
    {
        accountById.TryGetValue(t.AccountId, out var account);
        var signed = t.Direction == "Debit" ? -t.Amount : t.Amount;
        var category = t.CategoryId is { } cid && categoryById.TryGetValue(cid, out var path) ? path : "Uncategorized";
        return
        [
            t.PostedDate ?? t.EffectiveDate,
            t.Amount,
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
            t.Notes,
        ];
    }
}
