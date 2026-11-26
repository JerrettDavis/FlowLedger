using Bunit;
using FlowLedger.Web.Components.Pages;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace FlowLedger.Web.Tests.Pages;

public sealed class ErrorPagesTests : BunitContext
{
    public ErrorPagesTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Error_page_has_no_hosting_advice_and_links_home()
    {
        var cut = Render<Error>();

        cut.Markup.Should().NotContain("Development");
        cut.Markup.Should().NotContain("ASPNETCORE_ENVIRONMENT");
        cut.FindAll("h1").Should().ContainSingle().Which.TextContent.Should().Contain("Something went wrong");
        cut.Find("a[href='/']").TextContent.Should().Contain("Back to dashboard");
        cut.Markup.Should().Contain("Try again");
    }

    [Fact]
    public void NotFound_page_has_one_h1_and_links_home()
    {
        var cut = Render<NotFound>();

        cut.FindAll("h1").Should().ContainSingle().Which.TextContent.Should().Contain("Page not found");
        cut.Find("a[href='/']").Should().NotBeNull();
    }

    [Fact]
    public void Forbidden_page_does_not_claim_the_page_is_missing()
    {
        var cut = Render<Forbidden>();

        cut.FindAll("h1").Should().ContainSingle().Which.TextContent.Should().Contain("don't have access");
        cut.Markup.Should().NotContain("does not exist");
    }

    [Theory]
    [InlineData(403, "don't have access")]
    [InlineData(401, "don't have access")]
    [InlineData(404, "Page not found")]
    [InlineData(500, "Something went wrong")]
    public void Status_code_page_picks_the_message_for_the_code(int code, string heading)
    {
        var cut = Render<StatusCodePage>(p => p.Add(x => x.Code, code));

        cut.FindAll("h1").Should().ContainSingle().Which.TextContent.Should().Contain(heading);
    }
}
