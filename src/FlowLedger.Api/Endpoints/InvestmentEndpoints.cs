using FlowLedger.Application.Features.Investments;

namespace FlowLedger.Api.Endpoints;

/// <summary>Investment account growth over 30 days / 90 days / 1 year, with deposits and withdrawals separated out.</summary>
internal static class InvestmentEndpoints
{
    internal static IEndpointRouteBuilder MapInvestmentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/investments/performance", async (InvestmentPerformanceService service, DateOnly? from, DateOnly? to, CancellationToken ct) =>
                Results.Ok(await service.GetAsync(DateOnly.FromDateTime(DateTime.UtcNow), ct, from, to)))
            .WithTags("Investments").WithName("GetInvestmentPerformance")
            .WithSummary("Per-account gain over 30 days, 90 days and 1 year, excluding contributions")
            .RequireRateLimiting("api");

        return app;
    }
}
