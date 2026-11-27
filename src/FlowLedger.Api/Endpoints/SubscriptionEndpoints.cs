using FlowLedger.Application.Features.Subscriptions;

namespace FlowLedger.Api.Endpoints;

/// <summary>Detected recurring subscriptions with price-change flags (#70).</summary>
internal static class SubscriptionEndpoints
{
    internal static IEndpointRouteBuilder MapSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/subscriptions").WithTags("Subscriptions").RequireRateLimiting("api");

        group.MapGet("/", async (ListSubscriptionsHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(ct)))
        .WithName("ListSubscriptions")
        .WithSummary("Subscriptions found in the transactions (steady monthly or yearly charges), with price increases and decreases");

        return app;
    }
}
