using FlowLedger.Application.Features.HomeAssistant;
using FlowLedger.SharedKernel;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

/// <summary>
/// Home Assistant sharing: FlowLedger pushes alert events (bill due/paid, paycheck coming/landed, overdraft, budget) to a
/// Home Assistant webhook. Alerts only by default; amounts are an explicit opt-in. The webhook URL is write-only: no
/// endpoint ever returns it.
/// </summary>
internal static class HomeAssistantEndpoints
{
    internal sealed record SaveRequest(
        bool Enabled, string? WebhookUrl, bool ClearWebhook, bool ShareAmounts, bool IncludeNames, int LeadDays, string[]? Kinds);

    internal static IEndpointRouteBuilder MapHomeAssistantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/home-assistant").WithTags("Home Assistant").RequireRateLimiting("api");

        group.MapGet("/", async (IHomeAssistantStore store, ITenantContext tenant, CancellationToken ct) =>
            Results.Ok(new { settings = await store.GetAsync(tenant.TenantId, ct), kinds = HaEventKinds.All }))
        .WithName("GetHomeAssistantSettings")
        .WithSummary("Home Assistant sharing settings (never includes the webhook URL)");

        group.MapPut("/", async ([FromBody] SaveRequest r, IHomeAssistantStore store, ITenantContext tenant, CancellationToken ct) =>
        {
            string? url = null;
            if (!string.IsNullOrWhiteSpace(r.WebhookUrl))
            {
                if (!HomeAssistantWebhookUrl.TryValidate(r.WebhookUrl, out url, out var error))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["webhookUrl"] = [error!] });
                }
            }

            var kinds = (r.Kinds ?? [.. HaEventKinds.All]).Where(HaEventKinds.All.Contains).Distinct().ToList();
            if (kinds.Count == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["kinds"] = ["Choose at least one alert type to share."] });
            }

            var current = await store.GetAsync(tenant.TenantId, ct);
            if (r.Enabled && url is null && (r.ClearWebhook || !current.HasWebhook))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["webhookUrl"] = ["Add a webhook URL before turning sharing on."] });
            }

            await store.SaveAsync(tenant.TenantId, r.Enabled, url, r.ClearWebhook && url is null, r.ShareAmounts, r.IncludeNames,
                Math.Clamp(r.LeadDays, 0, 30), kinds, ct);
            return Results.Ok(await store.GetAsync(tenant.TenantId, ct));
        })
        .WithName("SaveHomeAssistantSettings")
        .WithSummary("Save Home Assistant sharing settings (the webhook URL is write-only)")
        .RequireRateLimiting("write");

        group.MapGet("/preview", async (HomeAssistantService service, CancellationToken ct) =>
            Results.Ok((await service.PreviewAsync(DateOnly.FromDateTime(DateTime.UtcNow), ct)).Select(HomeAssistantService.ToPayload)))
        .WithName("PreviewHomeAssistantEvents")
        .WithSummary("What would be sent right now, with amounts omitted (nothing is sent)");

        group.MapPost("/test", async (HomeAssistantService service, CancellationToken ct) =>
        {
            var error = await service.SendTestAsync(ct);
            return error is null
                ? Results.Ok(new { sent = true })
                : Results.Problem(title: "Home Assistant test failed", detail: error, statusCode: StatusCodes.Status502BadGateway);
        })
        .WithName("TestHomeAssistant")
        .WithSummary("Send a test event to Home Assistant")
        .RequireRateLimiting("write");

        group.MapPost("/send", async (HomeAssistantService service, CancellationToken ct) =>
            Results.Ok(await service.RunAsync(DateOnly.FromDateTime(DateTime.UtcNow), ct)))
        .WithName("SendHomeAssistantEvents")
        .WithSummary("Detect and send any new events now")
        .RequireRateLimiting("write");

        return app;
    }
}
