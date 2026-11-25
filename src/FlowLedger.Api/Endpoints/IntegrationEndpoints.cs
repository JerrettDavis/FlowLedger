using FlowLedger.Application.Features.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

/// <summary>
/// The base integration layer: connect FlowLedger to other services over REST, SOAP/WSDL or signed webhooks. Credentials and
/// signing secrets are write-only: no endpoint returns them (a webhook's signing secret is shown once, when it is created or rotated).
/// </summary>
internal static class IntegrationEndpoints
{
    internal static IEndpointRouteBuilder MapIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/integrations").WithTags("Integrations").RequireRateLimiting("api");

        group.MapGet("/", async (IntegrationService service, CancellationToken ct) => Results.Ok(new
        {
            integrations = await service.ListAsync(ct),
            canStoreSecrets = service.CanStoreSecrets,
            events = IntegrationEvents.All,
            limit = IntegrationService.MaxIntegrationsPerTenant,
        }))
        .WithName("ListIntegrations")
        .WithSummary("Integrations, never including credentials or signing secrets");

        group.MapPost("/", async ([FromBody] IntegrationSaveRequest r, IntegrationService service, CancellationToken ct) =>
            Saved(await service.SaveAsync(null, r, ct), StatusCodes.Status201Created))
        .WithName("CreateIntegration")
        .WithSummary("Create a REST, SOAP or webhook integration (a webhook's signing secret is returned once)")
        .RequireRateLimiting("write");

        group.MapPut("/{id:guid}", async (Guid id, [FromBody] IntegrationSaveRequest r, IntegrationService service, CancellationToken ct) =>
            Saved(await service.SaveAsync(id, r, ct), StatusCodes.Status200OK))
        .WithName("UpdateIntegration")
        .WithSummary("Update an integration (null credential keeps the stored one)")
        .RequireRateLimiting("write");

        group.MapDelete("/{id:guid}", async (Guid id, IntegrationService service, CancellationToken ct) =>
            await service.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound())
        .WithName("DeleteIntegration")
        .WithSummary("Delete an integration and its delivery log")
        .RequireRateLimiting("write");

        group.MapPost("/{id:guid}/rotate-secret", async (Guid id, IntegrationService service, CancellationToken ct) =>
        {
            var (integration, secret, error) = await service.RotateSigningSecretAsync(id, ct);
            return integration is null
                ? Results.Problem(title: "Couldn't rotate the signing secret", detail: error, statusCode: StatusCodes.Status422UnprocessableEntity)
                : Results.Ok(new { integration, signingSecret = secret });
        })
        .WithName("RotateIntegrationSecret")
        .WithSummary("Issue a new webhook signing secret (shown once)")
        .RequireRateLimiting("write");

        group.MapGet("/{id:guid}/deliveries", async (Guid id, IntegrationService service, CancellationToken ct) =>
            Results.Ok(await service.DeliveriesAsync(id, ct)))
        .WithName("ListIntegrationDeliveries")
        .WithSummary("Recent calls and deliveries (status and timing only, never bodies)");

        group.MapPost("/{id:guid}/test", async (Guid id, IntegrationService service, CancellationToken ct) =>
        {
            var (result, error) = await service.TestWebhookAsync(id, ct);
            return result is null ? Failed("Test failed", error) : Results.Ok(result);
        })
        .WithName("TestIntegrationWebhook")
        .WithSummary("Send a test event to a webhook")
        .RequireRateLimiting("write");

        group.MapPost("/{id:guid}/rest/call", async (Guid id, [FromBody] RestCallRequest r, IntegrationService service, CancellationToken ct) =>
        {
            var (result, error) = await service.CallRestAsync(id, r, ct);
            return result is null ? Failed("Request not sent", error) : Results.Ok(result);
        })
        .WithName("CallRestIntegration")
        .WithSummary("Send one request to a REST integration (base URL + path)")
        .RequireRateLimiting("write");

        group.MapPost("/{id:guid}/soap/operations", async (Guid id, IntegrationService service, CancellationToken ct) =>
            Results.Ok(await service.SoapOperationsAsync(id, ct)))
        .WithName("ListSoapOperations")
        .WithSummary("Fetch the WSDL and list its operations")
        .RequireRateLimiting("write");

        group.MapPost("/{id:guid}/soap/invoke", async (Guid id, [FromBody] SoapInvokeRequest r, IntegrationService service, CancellationToken ct) =>
        {
            var (result, error) = await service.SoapInvokeAsync(id, r, ct);
            return result is null ? Failed("Request not sent", error) : Results.Ok(result);
        })
        .WithName("InvokeSoapOperation")
        .WithSummary("Call one operation of a SOAP integration")
        .RequireRateLimiting("write");

        return app;
    }

    private static IResult Saved(IntegrationSaveResult result, int status)
    {
        if (result.Integration is null)
        {
            return Results.ValidationProblem(result.Errors ?? new Dictionary<string, string[]>());
        }

        var body = new { integration = result.Integration, signingSecret = result.NewSigningSecret };
        return status == StatusCodes.Status201Created ? Results.Created($"/api/integrations/{result.Integration.Id}", body) : Results.Ok(body);
    }

    private static IResult Failed(string title, string? detail)
        => Results.Problem(title: title, detail: detail, statusCode: StatusCodes.Status422UnprocessableEntity);
}
