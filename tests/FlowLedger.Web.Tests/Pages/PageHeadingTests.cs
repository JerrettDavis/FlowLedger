using FluentAssertions;

namespace FlowLedger.Web.Tests.Pages;

/// <summary>
/// Every routed page needs exactly one h1: Routes.razor's FocusHeadingOnNavigate targets "h1", so a page without one gives keyboard and
/// screen-reader users no focus move or announcement on navigation (axe: page-has-heading-one).
/// </summary>
public sealed class PageHeadingTests
{
    private static string PagesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "FlowLedger.Web", "Components", "Pages")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the repository root must be reachable from the test output folder");
        return Path.Combine(dir!.FullName, "src", "FlowLedger.Web", "Components", "Pages");
    }

    private static IEnumerable<string> RoutedPages() =>
        Directory.EnumerateFiles(PagesDirectory(), "*.razor")
            .Where(f => File.ReadLines(f).Any(l => l.TrimStart('﻿').StartsWith("@page ", StringComparison.Ordinal)));

    [Fact]
    public void Every_routed_page_declares_an_h1_directly_or_through_StatusMessage()
    {
        var missing = RoutedPages()
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return !text.Contains("<PageHeader") && !text.Contains("<h1") && !text.Contains("<StatusMessage");
            })
            .Select(Path.GetFileName)
            .ToList();

        missing.Should().BeEmpty("every page needs an h1 for FocusHeadingOnNavigate and the document outline");
    }

    [Fact]
    public void No_page_hand_rolls_its_own_header_row()
    {
        var offenders = RoutedPages()
            .Where(f => File.ReadAllText(f).Contains("HtmlTag=\"h1\""))
            .Select(Path.GetFileName)
            .ToList();

        offenders.Should().BeEmpty("headings come from PageHeader (title, subtitle, actions) or StatusMessage, never an ad-hoc MudText");
    }

    [Fact]
    public void Routed_pages_are_found()
        => RoutedPages().Should().HaveCountGreaterThan(20);
}
