using FlowLedger.Api.Errors;
using FlowLedger.Domain.Exceptions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Api.Tests.Security;

/// <summary>#90: domain invariant violations map to problem+json in one place instead of per-endpoint catch blocks.</summary>
public sealed class DomainExceptionMappingTests
{
    private static async Task<(bool Handled, int Status, string Body)> RunAsync(Exception ex)
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Response.Body = new MemoryStream();
        var handler = new ProviderExceptionHandler(services.GetRequiredService<IProblemDetailsService>());

        var handled = await handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        ctx.Response.Body.Position = 0;
        return (handled, ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task Invalid_status_transition_is_409()
    {
        var (handled, status, body) = await RunAsync(new InvalidStatusTransitionException("Posted", "Skipped", "Transaction"));
        handled.Should().BeTrue();
        status.Should().Be(409);
        body.Should().Contain("Invalid status transition");
    }

    [Fact]
    public async Task Other_domain_violations_are_400()
    {
        var (handled, status, _) = await RunAsync(new NegativeOrZeroAmountException("test"));
        handled.Should().BeTrue();
        status.Should().Be(400);
    }

    [Fact]
    public async Task Unexpected_exceptions_fall_through_to_the_default_handler_with_no_message_leak()
    {
        var (handled, _, body) = await RunAsync(new InvalidOperationException("secret internals"));
        handled.Should().BeFalse("only known exception types are mapped; everything else gets the generic 500");
        body.Should().NotContain("secret internals");
    }
}
