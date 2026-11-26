using FlowLedger.Application.Abstractions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Api.Errors;

/// <summary>
/// Maps a lost optimistic-concurrency race to RFC 7807 409 Conflict (#157), so a client holding a stale copy is told to
/// reload instead of silently overwriting a newer change. Handles both the application-level
/// <see cref="ConcurrencyConflictException"/> and a raw <see cref="DbUpdateConcurrencyException"/> from any endpoint.
/// </summary>
internal sealed class ConcurrencyExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    internal const string Detail = "This item was changed elsewhere. Reload it to see the latest version, then try again.";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not (ConcurrencyConflictException or DbUpdateConcurrencyException))
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
                Title = "Changed elsewhere",
                Detail = Detail,
            },
        });
    }
}
