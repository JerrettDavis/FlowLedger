using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace FlowLedger.Api.Tests.Security;

[Collection("ApiIntegration")]
public sealed class ProductionConfigGuardTests(FailClosedApiFactory factory)
{
    [Fact]
    public void Production_api_refuses_a_template_placeholder_api_key()
    {
        var act = () => factory
            .WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Api:Key"] = "your-api-key-here" })))
            .CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*placeholder*");
    }

    [Theory]
    [InlineData("your-mx-api-key-here", true)]
    [InlineData("changeme", true)]
    [InlineData("<POSTGRES_PASSWORD>", true)]
    [InlineData("3f9a1c7e5b2d4a8c9e0f1a2b3c4d5e6f", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Placeholder_detection(string? value, bool expected)
        => ConfigGuards.LooksLikePlaceholder(value).Should().Be(expected);
}
