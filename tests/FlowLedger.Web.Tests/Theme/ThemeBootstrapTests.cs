using FlowLedger.Web.Theme;
using FluentAssertions;

namespace FlowLedger.Web.Tests.Theme;

public sealed class ThemeBootstrapTests
{
    [Fact]
    public void Script_reads_the_stored_preference_key_used_by_the_interop_and_sets_data_theme_before_paint()
    {
        ThemeBootstrap.Script.Should().Contain("localStorage.getItem('flowledger-theme')");
        ThemeBootstrap.Script.Should().Contain("setAttribute('data-theme'");
        ThemeBootstrap.Script.Should().Contain("prefers-color-scheme: dark");
        ThemeBootstrap.Script.Should().Contain("colorScheme");
    }

    [Fact]
    public void Script_is_self_contained_and_cannot_throw_on_blocked_storage()
    {
        ThemeBootstrap.Script.Should().Contain("try").And.Contain("catch");
        ThemeBootstrap.Script.Should().NotContain("</script", "it is emitted inline in the head");
    }
}
