using FlowLedger.Application.Features.Accounts;
using FlowLedger.Domain.Exceptions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

internal static class AccountEndpoints
{
    internal static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accounts").WithTags("Accounts")
            .RequireRateLimiting("api");

        group.MapGet("/", async (ListAccountsHandler handler, CancellationToken ct, bool includeInactive = false) =>
            Results.Ok(await handler.HandleAsync(includeInactive, ct)))
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
        .WithSummary("Create a new account")
        .RequireRateLimiting("write");

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
        .WithSummary("Rename an account")
        .RequireRateLimiting("write");

        group.MapPut("/{id:guid}/type", async (
            Guid id,
            [FromBody] ChangeAccountTypeRequest request,
            ChangeAccountTypeHandler handler,
            IValidator<ChangeAccountTypeRequest> validator,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            try
            {
                var result = await handler.HandleAsync(id, request, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (DomainException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        })
        .WithName("ChangeAccountType")
        .WithSummary("Correct an account's type by hand; the type is pinned so provider sync never overwrites it")
        .RequireRateLimiting("write");

        group.MapPut("/{id:guid}/settings", async (
            Guid id,
            [FromBody] AccountSettingsRequest request,
            UpdateAccountSettingsHandler handler,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<FlowLedger.Domain.Aggregates.AccountMode>(request.Mode, ignoreCase: true, out _))
            {
                return Results.Problem("Mode must be 'Standard' or 'Holding'.", statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await handler.HandleAsync(id, request, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        })
        .WithName("UpdateAccountSettings")
        .WithSummary("Enable/disable an account, set its mode (Standard or Holding) and whether its transactions are hidden from everyday views")
        .RequireRateLimiting("write");

        group.MapDelete("/{id:guid}", async (Guid id, DeactivateAccountHandler handler, CancellationToken ct) =>
        {
            var found = await handler.HandleAsync(id, ct);
            return found ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeactivateAccount")
        .WithSummary("Deactivate (soft-delete) an account")
        .RequireRateLimiting("write");

        return app;
    }
}
