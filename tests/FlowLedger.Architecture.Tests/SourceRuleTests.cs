using System.Text.RegularExpressions;

namespace FlowLedger.Architecture.Tests;

/// <summary>
/// #110: rules the code actually relies on, enforced from the project files and sources so they also catch transitive
/// and string-based violations. Where today's code already violates a rule it sits on a ratchet allow-list: the list may
/// only shrink (a stale entry fails the test).
/// </summary>
public sealed class SourceRuleTests
{
    private static readonly string Root = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FlowLedger.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (FlowLedger.slnx) not found.");
    }

    private static string Src(string project) => Path.Combine(Root, "src", project);

    private static IEnumerable<string> SourceFiles(string project) =>
        Directory.EnumerateFiles(Src(project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));

    private static string Rel(string file) => Path.GetRelativePath(Root, file).Replace('\\', '/');

    // ── 1. Connector projects stay at the edge ───────────────────────────────

    [Theory]
    [InlineData("FlowLedger.Integrations.Mx")]
    [InlineData("FlowLedger.Integrations.Quiltt")]
    [InlineData("FlowLedger.Integrations.SimpleFin")]
    [InlineData("FlowLedger.Integrations.Simulated")]
    public void Connector_projects_do_not_reference_Application_Infrastructure_Api_or_Web(string project)
    {
        var csproj = File.ReadAllText(Path.Combine(Src(project), project + ".csproj"));
        var refs = Regex.Matches(csproj, @"<ProjectReference\s+Include=""[^""]*?([^\\/""]+)\.csproj""")
            .Select(m => m.Groups[1].Value)
            .ToList();

        var forbidden = refs.Where(r => r is "FlowLedger.Application" or "FlowLedger.Infrastructure" or "FlowLedger.Api"
            or "FlowLedger.Web" or "FlowLedger.Worker").ToList();

        Assert.True(forbidden.Count == 0, $"{project} must not reference: {string.Join(", ", forbidden)}");
    }

    // ── 2. Domain and Application stay free of host/infrastructure technology ─

    [Theory]
    [InlineData("FlowLedger.Domain")]
    [InlineData("FlowLedger.Application")]
    public void Domain_and_Application_do_not_use_Npgsql_Quartz_or_AspNetCore(string project)
    {
        var banned = new Regex(@"^\s*using\s+(Npgsql|Quartz|Microsoft\.AspNetCore)\b", RegexOptions.Multiline);
        var offenders = SourceFiles(project)
            .Where(f => banned.IsMatch(File.ReadAllText(f)))
            .Select(Rel)
            .ToList();

        Assert.True(offenders.Count == 0, $"Forbidden using in: {string.Join(", ", offenders)}");

        var csproj = File.ReadAllText(Path.Combine(Src(project), project + ".csproj"));
        Assert.DoesNotContain("Npgsql", csproj);
        Assert.DoesNotContain("Quartz", csproj);
    }

    // ── 3. Time comes from a TimeProvider, not the wall clock (ratchet) ──────

    /// <summary>Files that still read the wall clock directly. Remove an entry when the file is fixed (#127).</summary>
    private static readonly HashSet<string> WallClockAllowlist =
    [
        "src/FlowLedger.Domain/Aggregates/Transaction.cs",
        "src/FlowLedger.Domain/Aggregates/Account.cs",
        "src/FlowLedger.Domain/Aggregates/RecurringFlow.cs",
        "src/FlowLedger.Domain/Aggregates/Asset.cs",
        "src/FlowLedger.Domain/Events/DomainEvents.cs",
        "src/FlowLedger.Application/Features/Assets/AssetHandlers.cs",
    ];

    private static readonly Regex WallClock = new(@"DateTime(Offset)?\.(UtcNow|Now|Today)\b", RegexOptions.Compiled);

    private static List<string> WallClockUsers() =>
        new[] { "FlowLedger.Domain", "FlowLedger.Application" }
            .SelectMany(SourceFiles)
            .Where(f => WallClock.IsMatch(File.ReadAllText(f)))
            .Select(Rel)
            .ToList();

    [Fact]
    public void No_new_wall_clock_reads_in_Domain_or_Application()
    {
        var fresh = WallClockUsers().Where(f => !WallClockAllowlist.Contains(f)).ToList();
        Assert.True(fresh.Count == 0,
            $"New direct wall-clock use (inject TimeProvider instead): {string.Join(", ", fresh)}");
    }

    [Fact]
    public void Wall_clock_allowlist_has_no_stale_entries()
    {
        var users = WallClockUsers().ToHashSet();
        var stale = WallClockAllowlist.Where(f => !users.Contains(f)).ToList();
        Assert.True(stale.Count == 0, $"Remove fixed files from the allow-list: {string.Join(", ", stale)}");
    }

    // ── 4. Aggregate state is only changed through behaviour ─────────────────

    [Fact]
    public void Aggregate_roots_have_no_public_setters()
    {
        var domain = typeof(FlowLedger.Domain.Aggregates.Account).Assembly;
        var roots = domain.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetInterfaces().Any(i => i.Name == "IAggregateRoot"))
            .ToList();
        Assert.NotEmpty(roots);

        var offenders = roots
            .SelectMany(t => t.GetProperties().Where(p => p.SetMethod is { IsPublic: true }).Select(p => $"{t.Name}.{p.Name}"))
            .ToList();

        Assert.True(offenders.Count == 0, $"Public setters on aggregate roots: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Domain_events_are_sealed()
    {
        var domain = typeof(FlowLedger.Domain.Aggregates.Account).Assembly;
        var events = domain.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetInterfaces().Any(i => i.Name == "IDomainEvent"))
            .ToList();
        Assert.NotEmpty(events);

        var open = events.Where(t => !t.IsSealed).Select(t => t.Name).ToList();
        Assert.True(open.Count == 0, $"Unsealed domain events: {string.Join(", ", open)}");
    }

    // ── 5. No sync-over-async in product code (#116) ─────────────────────────

    [Fact]
    public void Product_code_does_not_block_on_tasks()
    {
        var offenders = Directory.EnumerateDirectories(Path.Combine(Root, "src"))
            .SelectMany(d => SourceFiles(Path.GetFileName(d)))
            .Where(f => File.ReadAllText(f).Contains(".GetAwaiter().GetResult()", StringComparison.Ordinal))
            .Select(Rel)
            .ToList();

        Assert.True(offenders.Count == 0, $"Sync-over-async in: {string.Join(", ", offenders)}");
    }

    // ── 6. Connector identity comes from ConnectorIds (#89) ──────────────────

    /// <summary>Files that still hold a bare connector literal (OpenAPI tag text or a private const). Shrink only.</summary>
    private static readonly HashSet<string> ConnectorLiteralAllowlist =
    [
        "src/FlowLedger.Api/Endpoints/DataEndpoints.cs",
        "src/FlowLedger.Infrastructure/DependencyInjection.cs", // display names in the descriptor list
        "src/FlowLedger.Api/Endpoints/ConnectorEndpoints.cs",     // display names in the descriptor list
        "src/FlowLedger.Api/Endpoints/MxEndpoints.cs",
        "src/FlowLedger.Api/Endpoints/QuilttEndpoints.cs",
    ];

    [Fact]
    public void Infrastructure_and_Api_use_ConnectorIds_not_bare_connector_names()
    {
        var literal = new Regex("\"(Simulated|SimpleFIN|MX|Quiltt)\"");
        var offenders = new[] { "FlowLedger.Infrastructure", "FlowLedger.Api" }
            .SelectMany(SourceFiles)
            .Where(f => literal.IsMatch(File.ReadAllText(f)))
            .Select(Rel)
            .Where(f => !ConnectorLiteralAllowlist.Contains(f))
            .ToList();

        Assert.True(offenders.Count == 0, $"Use ConnectorIds instead of bare names in: {string.Join(", ", offenders)}");
    }

    // ── 7. One constant-time string comparison (#100) ────────────────────────

    /// <summary>Byte-level HMAC comparisons that legitimately use FixedTimeEquals directly.</summary>
    private static readonly HashSet<string> FixedTimeAllowlist =
    [
        "src/FlowLedger.SharedKernel/ConstantTime.cs",
        "src/FlowLedger.Integrations.Mx/MxWebhookVerifier.cs",
        "src/FlowLedger.Integrations.Quiltt/QuilttWebhookVerifier.cs",
        "src/FlowLedger.Integrations.Simulated/SimulatedFinancialDataProvider.cs",
        "src/FlowLedger.Api/Tenancy/TenantAssertion.cs",
    ];

    [Fact]
    public void String_secrets_are_compared_with_ConstantTime_not_ad_hoc_FixedTimeEquals()
    {
        var offenders = Directory.EnumerateDirectories(Path.Combine(Root, "src"))
            .SelectMany(d => SourceFiles(Path.GetFileName(d)))
            .Where(f => File.ReadAllText(f).Contains("FixedTimeEquals(", StringComparison.Ordinal))
            .Select(Rel)
            .Where(f => !FixedTimeAllowlist.Contains(f))
            .ToList();

        Assert.True(offenders.Count == 0, $"Use ConstantTime.SecretEquals (or add a reasoned allow-list entry) in: {string.Join(", ", offenders)}");
    }
}
