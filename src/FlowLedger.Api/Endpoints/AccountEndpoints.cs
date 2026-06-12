using FlowLedger.Application.Features.Accounts;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

internal static class AccountEndpoints
{
    internal static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accounts").WithTags("Accounts");

        group.MapGet("/", async (ListAccountsHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(ct)))
            .WithName("ListAccounts")
            .WithSummary("List all active accounts for the current tenant");

        group.MapGet("/{id:guid}", async (Guid id, GetAccountHandler handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        })
        .WithName("GetAccount")
        .WithSummary("Get a single account by ID");

        group.MapPost("/", async (
            [FromBody] CreateAccountRequest request,
            CreateAccountHandler handler,
            IValidator<CreateAccountRequest> validator,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            var result = await handler.HandleAsync(request, ct);
            return Results.Created($"/api/accounts/{result.Id}", result);
        })
        .WithName("CreateAccount")
        .WithSummary("Create a new account");

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateAccountRequest request,
            UpdateAccountHandler handler,
            IValidator<UpdateAccountRequest> validator,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            var result = await handler.HandleAsync(id, request, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        })
        .WithName("UpdateAccount")
        .WithSummary("Rename an account");

        group.MapDelete("/{id:guid}", async (Guid id, DeactivateAccountHandler handler, CancellationToken ct) =>
        {
            var found = await handler.HandleAsync(id, ct);
            return found ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeactivateAccount")
        .WithSummary("Deactivate (soft-delete) an account");

        return app;
    }
}
