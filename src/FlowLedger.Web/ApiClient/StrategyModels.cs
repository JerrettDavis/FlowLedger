namespace FlowLedger.Web.ApiClient;

public sealed record MarketRatesDto(
    DateOnly AsOf, decimal Hysa, decimal MoneyMarket, decimal Cd6Month, decimal Cd1Year, decimal Cd2Year, decimal Cd5Year,
    decimal Mortgage30, decimal Mortgage15, decimal Auto48Month, decimal PersonalLoan, decimal CreditCard,
    decimal TypicalCheckingApy, decimal TypicalSavingsApy, List<string> Live, string Source);

public sealed record SuggestionDto(
    string Id, string Kind, string Severity, string Title, string Summary, decimal? AnnualImpact, double Confidence,
    List<string> Evidence, List<string> Steps, List<string> Assumptions, string? Simulator);

public sealed record StrategyReportDto(
    DateTimeOffset GeneratedAt, MarketRatesDto Rates, decimal LiquidCash, decimal AvgMonthlySpend, decimal AvgMonthlyIncome,
    double? SavingsRate, double? EmergencyMonths, List<SuggestionDto> Suggestions, string Disclaimer,
    CoachPlanDto? Coach = null, CashTrajectoryDto? Trajectory = null);

public sealed record GrowthPointDto(int Month, decimal Balance, decimal Contributed, decimal Interest);

public sealed record GrowthComparisonDto(List<GrowthPointDto> Baseline, List<GrowthPointDto> Scenario, decimal Difference);

public sealed record FireDto(
    decimal FireNumber, decimal CurrentInvested, decimal MonthlyContribution, int? MonthsToFire, decimal? FireAge,
    decimal ProjectedAtHorizon, decimal ProjectedAnnualIncome, bool Reachable, List<GrowthPointDto> Path);

public sealed record PastPointDto(string Month, decimal ExtraWealth);

public sealed record PastWhatIfDto(List<PastPointDto> Points, decimal TotalExtra, decimal FromSaving, decimal FromYield);

public sealed record RefinanceDto(
    decimal CurrentPayment, decimal NewPayment, decimal MonthlySavings, decimal ClosingCosts, int? BreakEvenMonths,
    decimal CurrentRemainingInterest, decimal NewTotalInterest, decimal LifetimeSavings, bool Worthwhile);

public sealed record LadderRungDto(string Label, int Months, decimal Amount, decimal Apy, decimal InterestAtMaturity);

public sealed record LadderPlanDto(List<LadderRungDto> Rungs, decimal BlendedApy, decimal AnnualInterest, decimal SavingsAnnualInterest, decimal Advantage);

public sealed record CoachPlanDto(string Headline, string? PriorityId, string PickedBy, List<string> Narrative, List<string> Order);

public sealed record TrajectoryPointDto(int MonthsAhead, decimal Expected, decimal Low, decimal High);

public sealed record CashTrajectoryDto(
    decimal MonthlyNetTrend, decimal MonthlyNetAverage, string Direction, decimal? RunwayMonths, int? MonthsToEmergencyTarget,
    List<TrajectoryPointDto> Points, string Summary);

public sealed record AccountRateDto(Guid AccountId, decimal RatePercent, int? RemainingMonths);

public sealed record GoalLeverDto(string Id, string Title, string Detail, int? MonthsToGoal, int? MonthsSaved, decimal? MonthlyImpact);

public sealed record GoalPlanDto(
    decimal Target, int? BaselineMonths, int? ScenarioMonths, int? MonthsSaved, decimal? RequiredMonthly, decimal? Shortfall,
    decimal Surplus, decimal ScenarioMonthly, List<decimal> Baseline, List<decimal> Scenario, List<GoalLeverDto> Levers, List<string> Callouts);
