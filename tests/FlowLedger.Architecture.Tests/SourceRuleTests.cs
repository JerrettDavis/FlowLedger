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

    [Theory]
    [InlineData("FlowLedger.Domain")]
    [InlineData("FlowLedger.Application")]
    public void Domain_and_Application_do_not_use_HttpClient(string project)
    {
        // HTTP transport (including the SOAP/WSDL client) lives in Infrastructure behind a port.
        var http = new Regex(@"System\.Net\.Http|HttpClient", RegexOptions.None);
        var offenders = SourceFiles(project)
            .Where(f => http.IsMatch(File.ReadAllText(f)))
            .Select(Rel)
            .ToList();

        Assert.True(offenders.Count == 0, $"HTTP types used in {project}: {string.Join(", ", offenders)}");
    }

    /// <summary>Async methods that deliberately take no token, with the reason. Adding to this list needs a reason.</summary>
    private static readonly HashSet<string> CancellationTokenExceptions = new(StringComparer.Ordinal)
    {
        // The caller's token travels inside the factory delegate; the cache itself never awaits on its own behalf.
        "TenantResultCache.GetOrAddAsync",
    };

    [Fact]
    public void Application_async_methods_take_a_CancellationToken()
    {
        var application = typeof(FlowLedger.Application.DependencyInjection).Assembly;
        var offenders = application.GetExportedTypes()
            .Where(t => t is { IsClass: true } && !t.IsAbstract || t.IsInterface)
            .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                                          | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName
                            && (m.ReturnType == typeof(Task) || m.ReturnType == typeof(ValueTask)
                                || (m.ReturnType.IsGenericType && (m.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
                                                                  || m.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>))))
                            && m.GetParameters().All(p => p.ParameterType != typeof(CancellationToken)))
                .Select(m => $"{t.Name}.{m.Name}"))
            .Where(m => !CancellationTokenExceptions.Contains(m))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0, $"Public async Application methods without a CancellationToken parameter: {string.Join(", ", offenders)}");
    }

    // ── 3. Time comes from a TimeProvider, not the wall clock (ratchet) ──────

    /// <summary>Files that still read the wall clock directly. Empty since #127: Domain and Application take time from a TimeProvider.</summary>
    private static readonly HashSet<string> WallClockAllowlist = [];

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

    /// <summary>
    /// Domain and Application never fall back to the system clock either: a missing <c>TimeProvider</c> is a wiring error, not a reason to
    /// read wall-clock time (#127). Time is a required argument of every factory, handler and service that stamps or compares it.
    /// </summary>
    private static readonly Regex SystemClockFallback = new(@"TimeProvider\.System\b", RegexOptions.Compiled);

    [Fact]
    public void Domain_and_Application_do_not_fall_back_to_TimeProvider_System()
    {
        var users = new[] { "FlowLedger.Domain", "FlowLedger.Application" }
            .SelectMany(SourceFiles)
            .Where(f => SystemClockFallback.IsMatch(File.ReadAllText(f)))
            .Select(Rel)
            .ToList();
        Assert.True(users.Count == 0, $"Take the TimeProvider as a required parameter instead of defaulting to TimeProvider.System: {string.Join(", ", users)}");
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
        "src/FlowLedger.Infrastructure/DependencyInjection.Connectors.cs", // display names in the descriptor list
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

    /// <summary>Sync and Application code asks a connector what it does (<c>ConnectorTraits</c>); it never names a connector (#89).</summary>
    [Fact]
    public void Sync_and_Application_branch_on_connector_traits_not_connector_identity()
    {
        var connectorProject = new Regex(@"FlowLedger\.Integrations\.(Simulated|Mx|Quiltt|SimpleFin)\b");
        var connectorId = new Regex(@"ConnectorIds\.|""(Simulated|SimpleFIN|MX|Quiltt)""");
        var offenders = SourceFiles("FlowLedger.Infrastructure").Where(f => Rel(f).Contains("/Sync/"))
            .Concat(SourceFiles("FlowLedger.Application"))
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return connectorProject.IsMatch(text) || connectorId.IsMatch(text);
            })
            .Select(Rel)
            .ToList();

        Assert.True(offenders.Count == 0, $"Declare a ConnectorTraits flag instead of naming a connector in: {string.Join(", ", offenders)}");
    }

    // ── 6a. Endpoints do not translate BCL exceptions to HTTP (#90) ──────────

    [Fact]
    public void Endpoints_do_not_catch_BCL_exceptions_the_central_handler_maps_typed_ones()
    {
        var bcl = new Regex(@"catch\s*\(\s*(ArgumentException|InvalidOperationException|KeyNotFoundException)\b|catch\s*\(\s*Exception\s+\w+\s*\)\s*when\s*\(\s*\w+\s+is\s+(ArgumentException|InvalidOperationException)");
        var offenders = SourceFiles("FlowLedger.Api")
            .Where(f => Rel(f).StartsWith("src/FlowLedger.Api/Endpoints/", StringComparison.Ordinal))
            .Where(f => bcl.IsMatch(File.ReadAllText(f)))
            .Select(Rel)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Throw a typed application exception (NotFound/RequestValidation/Conflict/UnprocessableRequest) and let ProviderExceptionHandler map it; do not catch BCL exceptions in: {string.Join(", ", offenders)}");
    }

    // ── 6b. No new oversized source files (#103) ─────────────────────────────

    private const int MaxSourceLines = 500;

    /// <summary>Files over the limit, with their current length as a ceiling. A file may shrink but not grow; one that fits must leave the list. Empty today (#103): keep it empty.</summary>
    private static readonly Dictionary<string, int> OversizedCeilings = new();

    [Fact]
    public void Source_files_stay_under_the_size_limit_or_hold_a_shrinking_ceiling()
    {
        var sizes = Directory.EnumerateDirectories(Path.Combine(Root, "src"))
            .SelectMany(d => SourceFiles(Path.GetFileName(d)))
            .ToDictionary(Rel, f => File.ReadLines(f).Count());

        var tooBig = sizes.Where(kv => kv.Value > MaxSourceLines
                && (!OversizedCeilings.TryGetValue(kv.Key, out var ceiling) || kv.Value > ceiling))
            .Select(kv => $"{kv.Key} ({kv.Value})").ToList();
        var stale = OversizedCeilings.Where(kv => !sizes.TryGetValue(kv.Key, out var n) || n <= MaxSourceLines)
            .Select(kv => kv.Key).ToList();

        Assert.True(stale.Count == 0, $"Remove files that now fit from the ceiling list: {string.Join(", ", stale)}");
        Assert.True(tooBig.Count == 0, $"Split files over {MaxSourceLines} lines (or do not grow a listed one): {string.Join(", ", tooBig)}");
    }

    // ── 7. One constant-time string comparison (#100) ────────────────────────

    /// <summary>Byte-level HMAC comparisons that legitimately use FixedTimeEquals directly.</summary>
    private static readonly HashSet<string> FixedTimeAllowlist =
    [
        "src/FlowLedger.SharedKernel/ConstantTime.cs",
        "src/FlowLedger.Integrations.Abstractions/HmacWebhookSignature.cs", // the one shared webhook HMAC comparison
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

    // ── No service locator on the request in the API (#94) ───────────────────

    [Fact]
    public void Api_does_not_pull_required_services_off_the_request()
    {
        var offenders = SourceFiles("FlowLedger.Api")
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"RequestServices\s*\.\s*GetRequiredService"))
            .Select(Rel)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Inject the dependency (endpoint parameter, constructor or an authorization policy) instead of HttpContext.RequestServices.GetRequiredService in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Endpoints_do_not_hand_roll_the_whole_endpoint_household_guard()
    {
        var offenders = SourceFiles("FlowLedger.Api")
            .Where(f => Rel(f).StartsWith("src/FlowLedger.Api/Endpoints/", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("HouseholdTenantOnlyFilter", StringComparison.Ordinal))
            .Select(Rel)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Whole-endpoint household access is the registered 'Household' authorization policy: use .RequireAuthorization(HouseholdPolicy.Name) in: {string.Join(", ", offenders)}");
    }

    // ── Options go through IOptions + ValidateOnStart, never hand-built defaults (#108) ─

    /// <summary>
    /// The only places that may build one of our own options types with <c>new X()</c> / <c>new X { }</c>: a factory that binds configuration
    /// into the fresh instance, and a bootstrap that runs before any DI container exists. Everything else takes the bound instance.
    /// </summary>
    private static readonly HashSet<string> OptionsConstructionAllowlist = new(StringComparer.Ordinal)
    {
        "src/FlowLedger.Infrastructure/Persistence/DatabaseDataSource.cs", // DatabaseConnectionOptions.From(configuration): new + Bind
        "src/FlowLedger.Web/Auth/AuthSetup.cs", // OIDC options are read from configuration before the host is built
    };

    private static readonly HashSet<string> FrameworkOptionsTypes = new(StringComparer.Ordinal)
    {
        "JsonSerializerOptions", "DialogOptions", "ParallelOptions", "DistributedCacheEntryOptions", "ScheduleJobOptions", "MemoryCacheEntryOptions",
        "FixedWindowRateLimiterOptions", "FileStreamOptions", "SessionOptions", "HealthCheckOptions",
    };

    [Fact]
    public void Options_are_never_constructed_with_hand_built_defaults_anywhere_in_src()
    {
        var sources = Directory.EnumerateDirectories(Path.Combine(Root, "src"))
            .SelectMany(d => SourceFiles(Path.GetFileName(d)))
            .ToDictionary(f => f, File.ReadAllText);

        // Parameterless construction of any *Options type except the framework ones listed here (they are configuration of a call, not bound settings).
        var ctor = new Regex(@"new\s+(?:[\w.]+\.)?(\w+Options)\s*(?:\(\s*\)|\{)");
        bool Offends(string text) => ctor.Matches(text).Any(m => !FrameworkOptionsTypes.Contains(m.Groups[1].Value));

        var offenders = sources
            .Where(kv => Offends(kv.Value) && !OptionsConstructionAllowlist.Contains(Rel(kv.Key)))
            .Select(kv => Rel(kv.Key))
            .ToList();
        var stale = OptionsConstructionAllowlist.Where(p => !Offends(File.ReadAllText(Path.Combine(Root, p)))).ToList();

        Assert.True(offenders.Count == 0,
            $"Take the bound options (AddOptions<T>().BindConfiguration(...).ValidateOnStart(), then inject IOptions<T>.Value) instead of new XOptions() / '?? new XOptions()' in: {string.Join(", ", offenders)}");
        Assert.True(stale.Count == 0, $"These files no longer build options by hand; remove them from the allowlist: {string.Join(", ", stale)}");
    }

    // ── Endpoint limits live in ApiLimits (#121) ─────────────────────────────

    private static readonly Regex NumericLiteral = new(@"(?<![\w.])\d[\d_]*(?:\.\d+)?(?:[lLmMuUfFdD]{0,2})(?![\w.])", RegexOptions.Compiled);

    [Fact]
    public void Endpoint_files_carry_no_numeric_literal_of_1000_or_more_outside_ApiLimits()
    {
        var offenders = new List<string>();
        foreach (var file in SourceFiles("FlowLedger.Api")
                     .Where(f => Rel(f).StartsWith("src/FlowLedger.Api/Endpoints/", StringComparison.Ordinal)
                         && !Rel(f).EndsWith("/ApiLimits.cs", StringComparison.Ordinal)))
        {
            var lineNo = 0;
            foreach (var raw in File.ReadLines(file))
            {
                lineNo++;
                // Drop strings, chars and comments so only code literals are judged.
                var code = Regex.Replace(raw, "\"(?:[^\"\\\\]|\\\\.)*\"|'(?:[^'\\\\]|\\\\.)'", "\"\"");
                var comment = code.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0)
                {
                    code = code[..comment];
                }

                foreach (Match m in NumericLiteral.Matches(code))
                {
                    var digits = new string(m.Value.Where(char.IsDigit).ToArray());
                    var whole = m.Value.Split('.')[0];
                    var wholeDigits = new string(whole.Where(char.IsDigit).ToArray());
                    if (wholeDigits.Length > 0 && decimal.TryParse(wholeDigits, System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= 1000
                        && digits.Length > 0)
                    {
                        offenders.Add($"{Rel(file)}:{lineNo}: {m.Value}");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            $"Name the limit in src/FlowLedger.Api/Endpoints/ApiLimits.cs instead of a literal: {string.Join("; ", offenders)}");
    }
}
