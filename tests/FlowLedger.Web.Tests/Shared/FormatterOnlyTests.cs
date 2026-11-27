using System.Text.RegularExpressions;
using FluentAssertions;

namespace FlowLedger.Web.Tests.Shared;

/// <summary>
/// Numbers on screen go through <c>Format</c> (#212) so the display currency and locale apply everywhere: a page that calls
/// <c>ToString("C")</c>, <c>"N0"</c> or <c>"P0"</c> itself bypasses them and shows a hard-wired "$" or en-US grouping.
/// </summary>
public sealed class FormatterOnlyTests
{
    // .ToString("C"|"C2"|"N0"|"N2"|"P"|"P0"|"P1") and interpolation holes such as {value:N0} or {value:C}. A bare "N" is the Guid
    // format (element ids), not a number format, so it is not matched.
    private static readonly Regex AdHocNumberFormat = new(
        """ToString\("(?:[CP]\d?|N\d)"\)|\{[^{}"]*:(?:[CP]\d?|N\d)\}""", RegexOptions.Compiled);

    [Fact]
    public void Razor_files_use_Format_for_money_counts_and_percentages()
    {
        var offenders = RepoFiles.Razor()
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(x => AdHocNumberFormat.IsMatch(x.Text))
            .Select(x => $"{x.File}:{x.Line}: {x.Text.Trim()}")
            .ToList();

        offenders.Should().BeEmpty("use Format.Money / Format.Count / Format.Number / Format.Ratio so the display settings apply");
    }
}
