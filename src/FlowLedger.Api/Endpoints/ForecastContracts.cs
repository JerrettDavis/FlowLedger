using FlowLedger.Application.Features.Forecasting;

namespace FlowLedger.Api.Endpoints;

// ─────────────────────────────────────────────────────────────────────────────
// Forecast API contract (flattened wire shape)
//
// The forecast endpoint MUST NOT serialize the domain model (ForecastResult)
// directly: it uses strongly-typed ID structs (AccountId), the Money value object,
// and DateOnlyRange — all of which System.Text.Json renders as nested JSON objects
// (e.g. {"value":"<guid>"}). The Web client deserializes into ForecastResultDto,
// whose AccountId is a plain Guid, StartingBalance is flattened to amount/currency,
// and Horizon is flattened to HorizonStart/HorizonEnd. Returning the domain model
// directly produced a JSON contract mismatch:
//   "The JSON value could not be converted to System.Guid.
//    Path: $.accountSeries[0].accountId"
//
// These DTOs mirror FlowLedger.Web.ApiClient.* exactly so the wire contract is
// stable and self-contained. ToResponse() flattens the domain model into them.
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class ForecastResponse
{
    public required Guid ForecastRunId { get; init; }
    public required DateOnly AsOf { get; init; }
    public required DateOnly HorizonStart { get; init; }
    public required DateOnly HorizonEnd { get; init; }
    public required IReadOnlyList<AccountForecastSeriesResponse> AccountSeries { get; init; }
    public required IReadOnlyList<AggregateForecastPointResponse> AggregateSeries { get; init; }
    public required IReadOnlyList<AccountLowWaterMarkResponse> LowWaterMarks { get; init; }
    public required AggregateLowWaterMarkResponse AggregateLowWaterMark { get; init; }
    public required IReadOnlyList<OverdraftWarningResponse> OverdraftWarnings { get; init; }
    public required IReadOnlyList<GoalAffordabilityResponse> GoalOutcomes { get; init; }
}

internal sealed class AccountForecastSeriesResponse
{
    public required Guid AccountId { get; init; }
    public required decimal StartingBalanceAmount { get; init; }
    public required string StartingBalanceCurrency { get; init; }
    public required IReadOnlyList<ForecastPointResponse> Points { get; init; }
}

internal sealed class ForecastPointResponse
{
    public required DateOnly Date { get; init; }
    public required decimal BalanceAmount { get; init; }
    public required string BalanceCurrency { get; init; }
    public required decimal NetChangeAmount { get; init; }
    public required IReadOnlyList<ForecastLineItemResponse> ContributingItems { get; init; }
}

internal sealed class ForecastLineItemResponse
{
    public required string Source { get; init; }
    public required Guid SourceId { get; init; }
    public required string Label { get; init; }
    public required decimal AmountValue { get; init; }
    public required string Direction { get; init; }
    public required decimal BalanceDeltaAmount { get; init; }
    public required bool IsActual { get; init; }
}

internal sealed class AggregateForecastPointResponse
{
    public required DateOnly Date { get; init; }
    public required decimal BalanceAmount { get; init; }
}

internal sealed class AccountLowWaterMarkResponse
{
    public required Guid AccountId { get; init; }
    public required decimal MinBalanceAmount { get; init; }
    public required DateOnly Date { get; init; }
}

internal sealed class AggregateLowWaterMarkResponse
{
    public required decimal MinBalanceAmount { get; init; }
    public required DateOnly Date { get; init; }
}

internal sealed class OverdraftWarningResponse
{
    public required Guid AccountId { get; init; }
    public required DateOnly FirstBreachDate { get; init; }
    public required decimal ProjectedBalanceAmount { get; init; }
}

internal sealed class GoalAffordabilityResponse
{
    public required Guid GoalId { get; init; }
    public required string Name { get; init; }
    public required decimal TargetAmount { get; init; }
    public required decimal CurrentBalance { get; init; }
    public required decimal RemainingAmount { get; init; }
    public required bool IsAffordable { get; init; }
    public DateOnly? AffordableByDate { get; init; }
    public decimal? RequiredMonthlyContribution { get; init; }
}

internal static class ForecastResponseMapper
{
    /// <summary>
    /// Flattens the domain <see cref="ForecastResult"/> into the stable wire contract.
    /// Unwraps strongly-typed IDs to their bare Guid, splits Money into amount/currency,
    /// and flattens the horizon range into explicit start/end dates.
    /// </summary>
    public static ForecastResponse ToResponse(this ForecastResult result) => new()
    {
        ForecastRunId = result.ForecastRunId,
        AsOf = result.AsOf,
        HorizonStart = result.Horizon.Start,
        // Horizon.End is nullable on the domain range, but the forecast handler always
        // constructs a bounded horizon; fall back to Start defensively.
        HorizonEnd = result.Horizon.End ?? result.Horizon.Start,
        AccountSeries = result.AccountSeries.Select(s => new AccountForecastSeriesResponse
        {
            AccountId = s.AccountId.Value,
            StartingBalanceAmount = s.StartingBalance.Amount,
            StartingBalanceCurrency = s.StartingBalance.Currency.Code,
            Points = s.Points.Select(p => new ForecastPointResponse
            {
                Date = p.Date,
                BalanceAmount = p.Balance.Amount,
                BalanceCurrency = p.Balance.Currency.Code,
                NetChangeAmount = p.NetChange.Amount,
                ContributingItems = p.ContributingItems.Select(i => new ForecastLineItemResponse
                {
                    Source = i.Source.ToString(),
                    SourceId = i.SourceId,
                    Label = i.Label,
                    AmountValue = i.Amount.Amount,
                    Direction = i.Direction.ToString(),
                    BalanceDeltaAmount = i.BalanceDelta.Amount,
                    IsActual = i.IsActual
                }).ToList()
            }).ToList()
        }).ToList(),
        AggregateSeries = result.AggregateSeries.Select(a => new AggregateForecastPointResponse
        {
            Date = a.Date,
            BalanceAmount = a.Balance.Amount
        }).ToList(),
        LowWaterMarks = result.LowWaterMarks.Select(l => new AccountLowWaterMarkResponse
        {
            AccountId = l.AccountId.Value,
            MinBalanceAmount = l.MinBalance.Amount,
            Date = l.Date
        }).ToList(),
        AggregateLowWaterMark = new AggregateLowWaterMarkResponse
        {
            MinBalanceAmount = result.AggregateLowWaterMark.MinBalance.Amount,
            Date = result.AggregateLowWaterMark.Date
        },
        OverdraftWarnings = result.OverdraftWarnings.Select(o => new OverdraftWarningResponse
        {
            AccountId = o.AccountId.Value,
            FirstBreachDate = o.FirstBreachDate,
            ProjectedBalanceAmount = o.ProjectedBalance.Amount
        }).ToList(),
        GoalOutcomes = result.GoalOutcomes.Select(g => new GoalAffordabilityResponse
        {
            GoalId = g.GoalId,
            Name = g.Name,
            TargetAmount = g.TargetAmount.Amount,
            CurrentBalance = g.CurrentBalance.Amount,
            RemainingAmount = g.RemainingAmount.Amount,
            IsAffordable = g.IsAffordable,
            AffordableByDate = g.AffordableByDate,
            RequiredMonthlyContribution = g.RequiredMonthlyContribution
        }).ToList()
    };
}
