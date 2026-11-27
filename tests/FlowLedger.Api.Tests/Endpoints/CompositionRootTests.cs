using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>#108: the composition root must be self-consistent: it validates on build and every handler is registered.</summary>
[Collection("ApiIntegration")]
public sealed class CompositionRootTests(FlowLedgerApiFactory factory)
{
    [Fact]
    public void Host_builds_with_ValidateOnBuild_and_ValidateScopes()
    {
        using var strict = factory.WithWebHostBuilder(b => b.UseDefaultServiceProvider(o =>
        {
            o.ValidateOnBuild = true;
            o.ValidateScopes = true;
        }));

        var act = () => strict.Services;

        act.Should().NotThrow("lifetime mismatches and missing registrations must fail at startup, not at first request");
    }

    [Fact]
    public void Every_Handler_in_Application_is_registered()
    {
        var handlers = typeof(FlowLedger.Application.DependencyInjection).Assembly.GetTypes() // handlers are internal (#121): the scan must see them too
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false, IsNested: false } && t.Name.EndsWith("Handler", StringComparison.Ordinal))
            .ToList();
        handlers.Should().NotBeEmpty();

        using var scope = factory.Services.CreateScope();
        var missing = handlers
            .Where(t => scope.ServiceProvider.GetService(t) is null)
            .Select(t => t.FullName)
            .ToList();

        missing.Should().BeEmpty("a handler that is not registered fails with a 500 the first time its endpoint is called");
    }
}
