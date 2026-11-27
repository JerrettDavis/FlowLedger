using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace FlowLedger.Architecture.Tests;

/// <summary>
/// #173: a test never constructs a wire contract by position. Contracts DTOs and requests, the Application request/DTO records
/// and the Web client DTOs are built through the builders in <c>FlowLedger.TestSupport/Builders</c> (and, for Web-only types,
/// <c>FlowLedger.Web.Tests/Builders</c>), so adding a field to a contract changes one builder instead of every test that
/// happens to build one.
///
/// This is a source scan (a reflection scan cannot see call sites): it looks for <c>new Type(arg, ...)</c> and the target-typed
/// <c>Type name = new(arg, ...)</c> in every test file outside the builders. Zero-argument construction and object-initializer
/// construction are allowed. <see cref="AllowList"/> may only shrink: an entry that no longer matches a construction fails
/// the test, so a converted file cannot quietly keep its exemption.
/// </summary>
public sealed class PositionalDtoConstructionTests
{
    /// <summary>"relative/path.cs|TypeName" of a construction still allowed. Empty: do not add entries, add or extend a builder.</summary>
    private static readonly HashSet<string> AllowList = new(StringComparer.Ordinal);

    [Fact]
    public void Tests_build_contracts_through_builders_not_positional_constructors()
    {
        var violations = Scan().ToList();

        var unexpected = violations.Where(v => !AllowList.Contains(v.Key)).Select(v => v.Describe()).Distinct().ToList();
        var stale = AllowList.Where(a => violations.All(v => v.Key != a)).ToList();

        unexpected.Should().BeEmpty(
            "contracts are built with a FlowLedger.TestSupport builder (AccountDtoBuilder.A().Named(...).Build()), not positionally; "
            + "add the builder or the missing With... method instead of an allow-list entry");
        stale.Should().BeEmpty("the allow-list may only shrink; remove entries whose construction was converted");
    }

    [Fact]
    public void The_contract_type_set_is_not_empty()
    {
        // Guards the guard: if reflection stops finding the records the scan above would pass vacuously.
        (ContractTypes().Count + WebTypes().Count).Should().BeGreaterThan(100);
    }

    private sealed record Violation(string Path, int Line, string TypeName)
    {
        public string Key => Path + "|" + TypeName;

        public string Describe() => $"{Path}:{Line} constructs {TypeName} positionally";
    }

    private static IEnumerable<Violation> Scan()
    {
        var root = FindRepoRoot();
        var testsRoot = Path.Combine(root, "tests");
        var shared = BuildPattern(ContractTypes());
        var web = BuildPattern(WebTypes());

        foreach (var file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(testsRoot, file).Replace('\\', '/');
            if (IsBuilderOrGenerated(relative))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var patterns = relative.StartsWith("FlowLedger.Web.Tests/", StringComparison.Ordinal) ? new[] { shared, web } : [shared];
            foreach (var pattern in patterns)
            {
                foreach (var match in pattern.Matches(text).Cast<Match>())
                {
                    var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                    yield return new Violation(relative, line, match.Groups["type"].Value);
                }
            }
        }
    }

    private static bool IsBuilderOrGenerated(string relative) =>
        relative.StartsWith("FlowLedger.TestSupport/", StringComparison.Ordinal)
        || relative.StartsWith("FlowLedger.Web.Tests/Builders/", StringComparison.Ordinal)
        || relative.Contains("/obj/", StringComparison.Ordinal)
        || relative.Contains("/bin/", StringComparison.Ordinal);

    // new Type(<something>   |   Type name = new(<something>   (a zero-argument call is "new Type()" and does not match).
    private static Regex BuildPattern(IReadOnlyCollection<string> names)
    {
        var alternation = string.Join('|', names.OrderByDescending(n => n.Length).Select(Regex.Escape));
        return new Regex(
            $@"new\s+(?<type>{alternation})\s*\(\s*(?!\))|\b(?<type>{alternation})\??\s+\w+\s*(?:\{{[^}}]*\}})?\s*=\s*new\(\s*(?!\))",
            RegexOptions.Compiled | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));
    }

    /// <summary>Contracts records plus the Application request/DTO records: built the same way from any test project.</summary>
    private static List<string> ContractTypes()
    {
        var contracts = typeof(FlowLedger.Contracts.AccountDto).Assembly.GetExportedTypes().Where(IsRecord);
        var application = typeof(FlowLedger.Application.Abstractions.IDomainEventDispatcher).Assembly.GetExportedTypes()
            .Where(t => IsRecord(t) && (t.Name.EndsWith("Request", StringComparison.Ordinal) || t.Name.EndsWith("Dto", StringComparison.Ordinal)));
        return contracts.Concat(application).Select(t => t.Name).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The Web client's own DTO records; only scanned in Web.Tests (other projects have unrelated types of the same name).</summary>
    private static List<string> WebTypes() =>
        typeof(FlowLedger.Web.ApiClient.FlowLedgerApiClient).Assembly.GetExportedTypes()
            .Where(t => IsRecord(t) && t.Namespace == "FlowLedger.Web.ApiClient")
            .Select(t => t.Name)
            .Distinct(StringComparer.Ordinal)
            .Except(ContractTypes())
            .ToList();

    private static bool IsRecord(Type t) => t.IsClass && !t.IsAbstract && t.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null;

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
