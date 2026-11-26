using System.Globalization;
using FlowLedger.Integrations.Abstractions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Errors;

/// <summary>
/// The single HTTP mapping for provider failures (ADR 0018). Any <see cref="ProviderException"/> becomes RFC 7807
/// ProblemDetails driven purely by <see cref="ProviderIssue"/>, so every connector behaves identically and no
/// endpoint needs its own catch block. Anything that is not a provider exception falls through to the default handler.
/// </summary>
internal sealed class ProviderExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is FlowLedger.Application.Abstractions.ReferencedEntityNotFoundException missing)
        {
            // A foreign id and a missing id are deliberately indistinguishable (#146).
            return await WriteAsync(httpContext, exception, new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = $"{missing.Kind} not found",
                Detail = missing.Message,
            });
        }

        if (exception is FlowLedger.Domain.Exceptions.DomainException domain)
        {
            // Invariant violations are client errors: 409 when the entity is in the wrong state, otherwise 400 (#90).
            var conflict = domain is FlowLedger.Domain.Exceptions.InvalidStatusTransitionException
                or FlowLedger.Domain.Exceptions.OccurrenceAlreadyMatchedException;
            return await WriteAsync(httpContext, exception, new ProblemDetails
            {
                Status = conflict ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest,
                Title = conflict ? "Conflict with the current state" : "Request violates a business rule",
                Detail = domain.Message,
            });
        }

        if (exception is not ProviderException pe)
        {
            return false;
        }

        if (pe is InvalidWebhookSignatureException)
        {
            return await WriteAsync(httpContext, exception, new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Invalid webhook signature",
            });
        }

        var issue = ProviderIssue.FromException(pe);
        var seconds = RetryAfterSeconds(issue);

        var problem = new ProblemDetails
        {
            Status = StatusFor(pe, issue),
            Title = TitleFor(pe, issue),
            // The issue message is already clean of secrets and response bodies (ADR 0018); a user-facing hint is preferred when present.
            Detail = pe is NeedsUserActionProviderException { UserHint: { Length: > 0 } hint } ? hint : issue.Message,
        };
        problem.Extensions["issueKind"] = issue.Kind.ToString();
        problem.Extensions["retryable"] = issue.Retryable;
        problem.Extensions["requiresUserAction"] = issue.RequiresUserAction;
        if (seconds is { } s)
        {
            problem.Extensions["retryAfterSeconds"] = s;
            httpContext.Response.Headers.RetryAfter = s.ToString(CultureInfo.InvariantCulture);
        }

        return await WriteAsync(httpContext, exception, problem);
    }

    private async ValueTask<bool> WriteAsync(HttpContext httpContext, Exception exception, ProblemDetails problem)
    {
        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    private static int StatusFor(ProviderException pe, ProviderIssue issue) => issue switch
    {
        _ when pe is ProviderCallBudgetExceededException => StatusCodes.Status429TooManyRequests,
        { Kind: ProviderIssueKind.RateLimited } => StatusCodes.Status429TooManyRequests,
        { RequiresUserAction: true } => StatusCodes.Status409Conflict,
        { Kind: ProviderIssueKind.InstitutionUnavailable or ProviderIssueKind.ProviderUnavailable or ProviderIssueKind.ServiceUnavailable }
            => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status502BadGateway,
    };

    private static string TitleFor(ProviderException pe, ProviderIssue issue) => issue switch
    {
        _ when pe is ProviderCallBudgetExceededException => "Provider call budget reached",
        { RequiresUserAction: true } => "Provider connection requires user action",
        { Kind: ProviderIssueKind.RateLimited } => "Provider rate limit reached",
        { Kind: ProviderIssueKind.InstitutionUnavailable or ProviderIssueKind.ProviderUnavailable or ProviderIssueKind.ServiceUnavailable }
            => "Provider temporarily unavailable",
        _ => "Provider error",
    };

    private static int? RetryAfterSeconds(ProviderIssue issue) =>
        issue.RetryAfter is { } at ? Math.Max(0, (int)Math.Ceiling((at - DateTimeOffset.UtcNow).TotalSeconds)) : null;
}
