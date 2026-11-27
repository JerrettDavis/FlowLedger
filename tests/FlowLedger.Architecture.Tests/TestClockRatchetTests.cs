using System.Text.RegularExpressions;
using FluentAssertions;

namespace FlowLedger.Architecture.Tests;

/// <summary>
/// Ratchet for issue #144: tests must not read the wall clock (month-end / midnight flakes). New direct reads fail the
/// build. The baseline is empty (#144): every provider, mapper and sync seam takes a TimeProvider, so tests pass a fixed one.
/// Do not add entries; give the code under test a TimeProvider seam instead.
/// Use <c>FlowLedger.TestSupport.TestClock</c> for a fixed instant instead.
/// E2E tests (real browser polling deadlines) and Web.Tests (owned separately) are not scanned.
/// </summary>
public sealed class TestClockRatchetTests
{
    private static readonly Regex ClockRead = new(@"\b(DateTime|DateTimeOffset)\.(UtcNow|Now|Today)\b", RegexOptions.Compiled);

    /// <summary>Path relative to tests/ (forward slashes) to number of direct clock reads still allowed.</summary>
    private static readonly Dictionary<string, int> Baseline = new();

    private static readonly string[] SkippedProjects = ["FlowLedger.E2E.Tests", "FlowLedger.Web.Tests"];

    private static string TestsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FlowLedger.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the repository root (FlowLedger.slnx) must be above the test binaries");
        return Path.Combine(dir!.FullName, "tests");
    }

    private static Dictionary<string, int> Scan()
    {
        var root = TestsRoot();
        var result = new Dictionary<string, int>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var parts = relative.Split('/');
            if (parts.Contains("bin") || parts.Contains("obj") || SkippedProjects.Contains(parts[0])
                || relative.EndsWith("TestClockRatchetTests.cs", StringComparison.Ordinal))
            {
                continue;
            }

            var count = File.ReadLines(file)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Sum(line => ClockRead.Matches(line).Count);
            if (count > 0)
            {
                result[relative] = count;
            }
        }

        return result;
    }

    [Fact]
    public void Tests_do_not_gain_new_wall_clock_reads()
    {
        var actual = Scan();

        var regressions = actual
            .Where(kv => !Baseline.TryGetValue(kv.Key, out var allowed) || kv.Value > allowed)
            .Select(kv => $"{kv.Key}: {kv.Value} read(s), baseline {(Baseline.TryGetValue(kv.Key, out var a) ? a : 0)}")
            .ToList();

        regressions.Should().BeEmpty("tests must use a fixed date (FlowLedger.TestSupport.TestClock) instead of DateTime.UtcNow/Now");
    }

    [Fact]
    public void The_clock_read_baseline_is_not_stale()
    {
        var actual = Scan();

        var stale = Baseline
            .Where(kv => !actual.TryGetValue(kv.Key, out var now) || now < kv.Value)
            .Select(kv => $"{kv.Key}: baseline {kv.Value}, now {(actual.TryGetValue(kv.Key, out var n) ? n : 0)}; lower or remove the baseline entry")
            .ToList();

        stale.Should().BeEmpty("the ratchet must tighten as clock reads are removed");
    }
}
