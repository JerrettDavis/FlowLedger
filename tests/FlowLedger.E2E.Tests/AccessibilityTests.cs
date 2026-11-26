namespace FlowLedger.E2E.Tests;

using System.Text.RegularExpressions;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using FluentAssertions;
using Microsoft.Playwright;

/// <summary>
/// axe-core sweep of every route in the navigation menu, in light and dark, on a desktop (1440 wide) and a phone (390 wide)
/// viewport. Serious and critical violations fail the run; console errors fail it too. Runs in the e2e workflow's
/// "ui-a11y" step (Category=A11y), separate from the functional E2E category so its result is visible on its own.
/// </summary>
[Collection("E2E Collection")]
[Trait("Category", "A11y")]
public class AccessibilityTests : E2ETestBase
{
    private static readonly string[] FailingImpacts = ["serious", "critical"];

    /// <summary>Every <c>Href</c> in NavMenu.razor plus the dashboard, so a new menu entry is swept automatically.</summary>
    public static IEnumerable<object[]> Routes()
    {
        var root = FindRepoRoot();
        var nav = File.ReadAllText(Path.Combine(root, "src", "FlowLedger.Web", "Components", "Layout", "NavMenu.razor"));
        var routes = Regex.Matches(nav, @"Href=""(?<href>[^""]*)""").Select(m => "/" + m.Groups["href"].Value.TrimStart('/'))
            .Distinct()
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();
        routes.Should().HaveCountGreaterThan(20, "the nav menu lists every top-level page");

        foreach (var route in routes)
        {
            foreach (var scheme in new[] { "light", "dark" })
            {
                foreach (var (name, width, height) in new[] { ("desktop", 1440, 900), ("phone", 390, 844) })
                {
                    yield return [route, scheme, name, width, height];
                }
            }
        }
    }

    [Theory(DisplayName = "Accessibility: no serious or critical axe violations")]
    [MemberData(nameof(Routes))]
    public async Task Route_has_no_serious_axe_violations(string route, string scheme, string viewport, int width, int height)
    {
        if (ShouldSkip)
        {
            return;
        }

        await Page!.SetViewportSizeAsync(width, height);
        await Page.EmulateMediaAsync(new PageEmulateMediaOptions
        {
            ColorScheme = scheme == "dark" ? ColorScheme.Dark : ColorScheme.Light,
        });

        await NavigateAsync(route);
        await WaitForLoadAsync();
        // The interactive circuit renders after network idle; give the page a moment to settle before auditing.
        await Page.WaitForTimeoutAsync(1_000);

        var results = await Page.RunAxe(new AxeRunOptions
        {
            RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "best-practice"] },
        });

        var serious = results.Violations.Where(v => FailingImpacts.Contains(v.Impact)).ToList();
        serious.Should().BeEmpty(
            $"{route} ({scheme}, {viewport}) should have no serious or critical accessibility violations:\n" +
            string.Join("\n", serious.Select(v => $"- {v.Id} [{v.Impact}] {v.Help}: {string.Join(" | ", v.Nodes.Take(3).Select(n => n.Target.ToString()))}")));

        if (viewport == "phone")
        {
            var overflow = await Page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth + 1");
            overflow.Should().BeFalse($"{route} must not scroll sideways on a phone (tables scroll inside their own container)");
        }

        AssertNoPageErrors();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FlowLedger.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (FlowLedger.slnx) not found.");
    }
}

/// <summary>Keyboard access to the chrome and to transaction row actions (#217).</summary>
[Collection("E2E Collection")]
[Trait("Category", "A11y")]
public class KeyboardAccessTests : E2ETestBase
{
    [Fact(DisplayName = "Keyboard: the skip link is the first tab stop and moves focus to main")]
    public async Task Skip_link_is_first_and_focuses_main()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/");
        await WaitForLoadAsync();

        await Page!.Keyboard.PressAsync("Tab");
        var first = await Page.EvaluateAsync<string>("document.activeElement?.textContent?.trim() ?? ''");
        first.Should().Contain("Skip to main content");

        await Page.Keyboard.PressAsync("Enter");
        var focusedId = await Page.EvaluateAsync<string>("document.activeElement?.id ?? ''");
        focusedId.Should().Be("main-content");
    }

    [Fact(DisplayName = "Keyboard: Tab reaches Rename, Note and Split on a transaction row with a visible focus ring")]
    public async Task Row_actions_are_reachable_by_keyboard_with_a_visible_ring()
    {
        if (ShouldSkip)
        {
            return;
        }

        await NavigateAsync("/transactions");
        (await WaitForGridDataAsync("[aria-label='Transactions table']")).Should().BeGreaterThan(0);

        var seen = new HashSet<string>();
        var ringWidths = new List<double>();
        for (var i = 0; i < 80 && seen.Count < 3; i++)
        {
            await Page!.Keyboard.PressAsync("Tab");
            var info = await Page.EvaluateAsync<string[]>(
                "(() => { const e = document.activeElement; if (!e) return ['', '0']; " +
                "return [e.getAttribute('aria-label') ?? '', getComputedStyle(e).outlineWidth]; })()");
            var label = info[0];
            if (label.StartsWith("Rename merchant", StringComparison.Ordinal)
                || label.StartsWith("Note for", StringComparison.Ordinal)
                || label.StartsWith("Split ", StringComparison.Ordinal))
            {
                seen.Add(label.Split(' ')[0]);
                ringWidths.Add(double.Parse(info[1].Replace("px", ""), System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        seen.Should().BeEquivalentTo(["Rename", "Note", "Split"], "all three row actions must be in the keyboard order");
        ringWidths.Should().OnlyContain(w => w > 0, "the focused action must show an outline");
    }
}
