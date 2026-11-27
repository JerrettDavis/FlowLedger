using FlowLedger.Application.Abstractions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Errors;

/// <summary>Maps a per-tenant collection cap (<see cref="TenantQuotaExceededException"/>, #166) to RFC 7807 409 Conflict.</summary>
internal sealed class QuotaExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not TenantQuotaExceededException quota)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Limit reached",
                Detail = quota.Message,
            },
        });
    }
}
