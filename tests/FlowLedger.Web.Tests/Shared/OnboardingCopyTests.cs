using FlowLedger.Web.Shared;
using FluentAssertions;

namespace FlowLedger.Web.Tests.Shared;

public sealed class OnboardingCopyTests
{
    [Theory]
    [InlineData("production")]
    [InlineData("unknown")]
    [InlineData(null)]
    public void Non_demo_environments_never_mention_demo_data_or_the_demo_button(string? environment)
    {
        var text = OnboardingCopy.LoadData(environment);

        text.Should().NotContainEquivalentOf("demo");
        text.Should().NotContain("Connect & Sync");
    }

    [Fact]
    public void Production_points_at_connect_bank()
    {
        OnboardingCopy.LoadData("production").Should().Contain("Connect bank");
    }

    [Theory]
    [InlineData("demo", "demo data")]
    [InlineData("sandbox", "sandbox data")]
    public void Demo_and_sandbox_point_at_the_button_that_exists_for_them(string environment, string expected)
    {
        var text = OnboardingCopy.LoadData(environment);

        text.Should().Contain("Connect & Sync").And.Contain(expected);
    }

    [Fact]
    public void No_page_hard_codes_the_demo_only_instruction()
    {
        foreach (var file in RepoFiles.Razor().Where(f => !f.EndsWith("MainLayout.razor", StringComparison.Ordinal)))
        {
            File.ReadAllText(file).Should().NotContain("load demo data", $"{Path.GetFileName(file)} must use OnboardingCopy");
        }
    }

    [Fact]
    public void Suffix_is_appended_after_the_instruction()
    {
        OnboardingCopy.LoadData("production", "then run a forecast").Should().EndWith("then run a forecast.");
    }
}
