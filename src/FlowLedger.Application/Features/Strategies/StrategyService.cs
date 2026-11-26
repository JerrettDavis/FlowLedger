using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Features.Insights;
using FlowLedger.Application.Features.Investments;
using FlowLedger.Application.Features.Transactions;

namespace FlowLedger.Application.Features.Strategies;

public sealed record PastWhatIfRequest(
    int Months, decimal ExtraMonthly, string? CutCategory, decimal CutPercent,
    decimal IdleCash, decimal FromApy, decimal ToApy, decimal GrowthApy);

/// <summary>Loads the user's ledger, reduces it to <see cref="StrategyInputs"/>, and runs the engine and simulators.</summary>
public sealed class StrategyService(
    ITransactionRepository repo, IAccountRepository accounts, ICategoryRepository categories, IMarketRateProvider rateProvider,
    IAccountRateStore rateStore, FlowLedger.SharedKernel.ITenantContext tenant, ILocalLlm? llm = null, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private const int MaxTransactions = 50_000;
    private const int TransferPadDays = 14;

    public async Task<StrategyReport> GetReportAsync(CancellationToken ct = default)
    {
        var data = await LoadAsync(12, ct);
        var report = StrategyEngine.Analyze(data, _time.GetUtcNow());
        var trajectory = CashTrajectoryProjector.Project(data.Months, report.LiquidCash, report.AvgMonthlySpend);
        var coach = await StrategyCoach.PlanAsync(report, llm, ct);
        return report with { Coach = coach, Trajectory = trajectory };
    }

    public async Task<IReadOnlyList<AccountRate>> ListAccountRatesAsync(CancellationToken ct = default) =>
        await rateStore.ListAsync(tenant.TenantId, ct);

    /// <summary>Saves a rate for one of the caller's own accounts; false when the account doesn't exist for this tenant.</summary>
    public async Task<bool> SaveAccountRateAsync(AccountRate rate, CancellationToken ct = default)
    {
        if (await accounts.GetByIdAsync(FlowLedger.Domain.ValueObjects.AccountId.From(rate.AccountId), ct) is null)
        {
            return false;
        }

        await rateStore.UpsertAsync(tenant.TenantId, rate, ct);
        return true;
    }

    public Task RemoveAccountRateAsync(Guid accountId, CancellationToken ct = default) =>
        rateStore.RemoveAsync(tenant.TenantId, accountId, ct);

    public Task<MarketRates> GetRatesAsync(CancellationToken ct = default) => rateProvider.GetAsync(ct);

    public async Task<PastWhatIfResult> PastWhatIfAsync(PastWhatIfRequest r, CancellationToken ct = default)
    {
        var months = Math.Clamp(r.Months, 1, 60);
        var data = await LoadAsync(months, ct);
        var ordered = data.Months.OrderBy(m => m.Month, StringComparer.Ordinal).TakeLast(months).ToList();
        decimal cutFactor = Math.Clamp(r.CutPercent, 0, 100) / 100m;
        IReadOnlyDictionary<string, decimal>? cat = null;
        if (!string.IsNullOrWhiteSpace(r.CutCategory))
        {
            cat = data.CategoryByMonth.FirstOrDefault(kv => kv.Key.Equals(r.CutCategory, StringComparison.OrdinalIgnoreCase)).Value;
        }

        var extra = ordered
            .Select(m => (m.Month, Extra: Math.Max(0, r.ExtraMonthly) + (cat is not null && cat.TryGetValue(m.Month, out var spent) ? spent * cutFactor : 0m)))
            .ToList();
        return Simulators.PastWhatIf(extra, r.GrowthApy, Math.Max(0, r.IdleCash), r.FromApy, r.ToApy);
    }

    public async Task<GoalPlan> PlanGoalAsync(GoalRequest r, CancellationToken ct = default)
    {
        var data = await LoadAsync(12, ct);
        var recent = data.Months.TakeLast(6).ToList();
        var income = recent.Count > 0 ? recent.Average(m => m.Income) : 0m;
        var spend = recent.Count > 0 ? recent.Average(m => m.Spend) : 0m;
        var debts = data.Accounts.Where(a => a.IsLiability && a.Rate is > 0 && a.Balance > 0)
            .Select(a => new GoalDebt(a.Name, a.Balance, a.Rate!.Value, a.RemainingMonths ?? 60)).ToList();
        return GoalPlanner.Plan(r, new GoalContext(income, spend, debts), data.Rates);
    }

    private async Task<StrategyInputs> LoadAsync(int months, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        var firstOfThisMonth = new DateOnly(today.Year, today.Month, 1);
        var from = firstOfThisMonth.AddMonths(-months);
        var to = firstOfThisMonth.AddDays(-1); // complete months only

        var accountEntities = await accounts.ListAsync(ct);
        var excluded = await AccountVisibility.ExcludedIdsAsync(accounts, null, ct);
        var catPaths = (await categories.ListAsync(ct)).Select(CategoryMapper.ToDto).ToDictionary(c => c.Id, c => c.Path);
        var typeById = accountEntities.ToDictionary(a => a.Id, a => a.AccountType.ToString());

        var loaded = await repo.ListAsync(null, from.AddDays(-TransferPadDays), to.AddDays(TransferPadDays), 0, MaxTransactions, ct);
        var dtos = loaded.Select(TransactionMapper.ToDto).Where(t => !excluded.Contains(t.AccountId)).ToList();
        var transfers = TransferDetector.Detect(dtos, typeById);

        var income = new Dictionary<string, decimal>();
        var spend = new Dictionary<string, decimal>();
        var byCat = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.OrdinalIgnoreCase);
        var investmentAccounts = accountEntities.Where(InvestmentExclusion.IsInvestmentAccount).Select(a => a.Id).ToHashSet();

        foreach (var t in dtos)
        {
            var date = t.PostedDate ?? t.EffectiveDate;
            if (date < from || date > to || !FlowLedger.Domain.Aggregates.TransactionStatusExtensions.IsRealStatus(t.Status) || transfers.Contains(t.Id) || investmentAccounts.Contains(t.AccountId))
            {
                continue;
            }

            var path = t.CategoryId is { } id && catPaths.TryGetValue(id, out var p) ? p : SpendingInsightsAnalyzer.Uncategorized;
            if (SpendingInsightsAnalyzer.IsNonSpending(path) || InvestmentExclusion.IsInvestmentCategory(path))
            {
                continue;
            }

            var key = $"{date.Year:0000}-{date.Month:00}";
            if (t.Direction == "Credit")
            {
                income[key] = income.GetValueOrDefault(key) + t.Amount;
            }
            else
            {
                spend[key] = spend.GetValueOrDefault(key) + t.Amount;
                var top = path.Split('/', 2)[0].Trim();
                if (!byCat.TryGetValue(top, out var m))
                {
                    byCat[top] = m = new Dictionary<string, decimal>();
                }

                m[key] = m.GetValueOrDefault(key) + t.Amount;
            }
        }

        var monthKeys = Enumerable.Range(0, months)
            .Select(i => from.AddMonths(i))
            .Select(d => $"{d.Year:0000}-{d.Month:00}")
            .ToList();
        var flows = monthKeys.Select(k => new MonthFlow(k, income.GetValueOrDefault(k), spend.GetValueOrDefault(k)))
            // Drop leading months before any activity (new ledger) so averages aren't diluted by empty history.
            .SkipWhile(f => f.Income == 0 && f.Spend == 0)
            .ToList();

        var userRates = (await rateStore.ListAsync(tenant.TenantId, ct)).ToDictionary(r => r.AccountId);
        var snapshots = accountEntities.Where(a => a.IsActive).Select(a => new AccountSnapshot(
            a.Id, a.Name, a.AccountType.ToString(), a.CurrentBalance.Amount,
            InvestmentExclusion.IsInvestmentAccount(a), Account_IsLiability(a),
            userRates.TryGetValue(a.Id, out var ur) ? ur.RatePercent : null,
            userRates.TryGetValue(a.Id, out var ur2) ? ur2.RemainingMonths : null)).ToList();

        return new StrategyInputs(
            snapshots, flows,
            byCat.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, decimal>)kv.Value),
            await rateProvider.GetAsync(ct), today);
    }

    private static bool Account_IsLiability(FlowLedger.Domain.Aggregates.Account a) =>
        FlowLedger.Domain.Aggregates.Account.IsLiabilityType(a.AccountType);
}
