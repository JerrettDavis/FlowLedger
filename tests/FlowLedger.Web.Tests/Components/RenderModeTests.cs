using System.Text.RegularExpressions;
using FluentAssertions;

namespace FlowLedger.Web.Tests.Components;

public sealed class RenderModeTests
{
    [Fact]
    public void Routes_sets_the_interactive_render_mode_once_for_every_page()
    {
        RepoFiles.Read("Components/Routes.razor").Should().MatchRegex(@"(?m)^@rendermode InteractiveServer");
    }

    [Fact]
    public void Pages_do_not_repeat_the_render_mode_set_by_Routes()
    {
        var offenders = RepoFiles.Razor()
            .Where(f => !f.EndsWith("Routes.razor", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"(?m)^@rendermode\b"))
            .Select(Path.GetFileName)
            .ToList();

        offenders.Should().BeEmpty("Routes.razor already makes the whole router interactive");
    }

    [Fact]
    public void Program_does_not_register_the_unused_WebAssembly_render_mode()
    {
        var program = RepoFiles.Read("Program.cs");
        program.Should().NotContain("WebAssembly");
        program.Should().Contain("AddInteractiveServerRenderMode");
    }
}
