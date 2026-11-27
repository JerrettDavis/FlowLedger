using FlowLedger.Api.Errors;
using FlowLedger.Application.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>#166: a per-tenant collection cap surfaces as 409 Conflict with a readable detail, and nothing else is swallowed.</summary>
public sealed class QuotaExceptionHandlerTests
{
    private sealed class CapturingProblems : IProblemDetailsService
    {
        public ProblemDetailsContext? Written { get; private set; }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            Written = context;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            Written = context;
            return ValueTask.FromResult(true);
        }
    }

    [Fact]
    public async Task Quota_exception_becomes_409_with_the_limit_in_the_detail()
    {
        var problems = new CapturingProblems();
        var http = new DefaultHttpContext();

        var handled = await new QuotaExceptionHandler(problems).TryHandleAsync(http, new TenantQuotaExceededException("saved views and reports", 200), default);

        handled.Should().BeTrue();
        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        problems.Written!.ProblemDetails.Detail.Should().Contain("200").And.Contain("saved views and reports");
    }

    [Fact]
    public async Task Other_exceptions_are_not_handled()
    {
        var handled = await new QuotaExceptionHandler(new CapturingProblems())
            .TryHandleAsync(new DefaultHttpContext(), new InvalidOperationException("boom"), default);

        handled.Should().BeFalse();
    }
}
