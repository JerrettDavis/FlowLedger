namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

    public Task<StrategyReportDto?> GetStrategiesAsync(CancellationToken ct = default) =>
        StrategyCallAsync<StrategyReportDto>("load strategies", HttpMethod.Get, "/api/strategies", null, ct);

    public Task<List<AccountRateDto>?> GetAccountRatesAsync(CancellationToken ct = default) =>
        StrategyCallAsync<List<AccountRateDto>>("load account rates", HttpMethod.Get, "/api/strategies/account-rates", null, ct);

    public async Task SaveAccountRateAsync(Guid accountId, decimal ratePercent, int? remainingMonths, CancellationToken ct = default)
    {
        await ExecuteAsync("save the account rate", async () =>
        {
            var response = await _http.PutAsJsonAsync($"/api/strategies/account-rates/{accountId}", new { ratePercent, remainingMonths }, ct);
            await ThrowIfFailedAsync(response, "save the rate", ct);

            return true;
        });
    }

    public async Task RemoveAccountRateAsync(Guid accountId, CancellationToken ct = default)
    {
        await ExecuteAsync("remove the account rate", async () =>
        {
            var response = await _http.DeleteAsync($"/api/strategies/account-rates/{accountId}", ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't remove the rate (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return true;
        });
    }

    public Task<GrowthComparisonDto?> SimulateGrowthAsync(decimal start, decimal baseMonthly, decimal baseApy, decimal scenarioMonthly, decimal scenarioApy, int years, CancellationToken ct = default) =>
        StrategyCallAsync<GrowthComparisonDto>("run the growth simulation", HttpMethod.Post, "/api/strategies/simulate/growth",
            new { start, baseMonthly, baseApy, scenarioMonthly, scenarioApy, years }, ct);

    public Task<FireDto?> SimulateFireAsync(decimal invested, decimal monthlyContribution, decimal annualSpend, decimal withdrawalRate, decimal realReturn, decimal? age, int years, CancellationToken ct = default) =>
        StrategyCallAsync<FireDto>("run the FIRE projection", HttpMethod.Post, "/api/strategies/simulate/fire",
            new { invested, monthlyContribution, annualSpend, withdrawalRate, realReturn, age, years }, ct);

    public Task<PastWhatIfDto?> SimulatePastAsync(int months, decimal extraMonthly, string? cutCategory, decimal cutPercent, decimal idleCash, decimal fromApy, decimal toApy, decimal growthApy, CancellationToken ct = default) =>
        StrategyCallAsync<PastWhatIfDto>("run the what-if", HttpMethod.Post, "/api/strategies/simulate/past",
            new { months, extraMonthly, cutCategory, cutPercent, idleCash, fromApy, toApy, growthApy }, ct);

    public Task<RefinanceDto?> SimulateRefinanceAsync(decimal balance, decimal currentRate, int remainingMonths, decimal newRate, int newTermMonths, decimal closingCosts, CancellationToken ct = default) =>
        StrategyCallAsync<RefinanceDto>("run the refinance calculation", HttpMethod.Post, "/api/strategies/simulate/refinance",
            new { balance, currentRate, remainingMonths, newRate, newTermMonths, closingCosts }, ct);

    public Task<GoalPlanDto?> SimulateGoalAsync(
        decimal target, decimal saved, decimal monthly, decimal apy, int deadlineMonths, decimal extraMonthly,
        decimal incomeRaisePct, decimal spendCutPct, decimal lumpSum, decimal scenarioApy, CancellationToken ct = default) =>
        StrategyCallAsync<GoalPlanDto>("plan the goal", HttpMethod.Post, "/api/strategies/simulate/goal",
            new { target, saved, monthly, apy, deadlineMonths, extraMonthly, incomeRaisePct, spendCutPct, lumpSum, scenarioApy }, ct);

    public Task<LadderPlanDto?> SimulateLadderAsync(decimal amount, CancellationToken ct = default) =>
        StrategyCallAsync<LadderPlanDto>("build the CD ladder", HttpMethod.Post, "/api/strategies/simulate/ladder", new { amount }, ct);

    public async Task<DebtPlanDto?> PlanDebtAsync(List<DebtRowDto> debts, decimal extraMonthly, CancellationToken ct = default)
    {
        return await ExecuteAsync("plan debt payoff", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/debt/plan", new { debts, extraMonthly }, ct);
            await ThrowIfFailedAsync(response, "compute plan", ct);

            return await response.Content.ReadFromJsonAsync<DebtPlanDto>(JsonOptions, ct);
        });
    }

    public async Task<List<AlertDto>?> GetAlertsAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("read alerts", async () =>
        {
            var response = await _http.GetAsync("/api/alerts", ct);
            await ThrowIfFailedAsync(response, "read alerts", ct);
            return await response.Content.ReadFromJsonAsync<List<AlertDto>>(JsonOptions, ct);
        });
    }
}
