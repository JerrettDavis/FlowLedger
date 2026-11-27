using System.Reflection;
using FluentAssertions;

namespace FlowLedger.Architecture.Tests;

/// <summary>
/// #121: the public type surface of Application and Infrastructure is a reviewed contract. Types are internal by default
/// (tests get InternalsVisibleTo); a type only becomes public when another assembly genuinely consumes it, and that has to
/// show up in the baseline diff. To accept an intentional change, regenerate with
/// <c>FLOWLEDGER_UPDATE_PUBLIC_API=1 dotnet test tests/FlowLedger.Architecture.Tests --filter PublicApiSurfaceTests</c>
/// and commit the updated <c>PublicApi/*.txt</c> files.
/// </summary>
public sealed class PublicApiSurfaceTests
{
    [Fact]
    public void Application_public_surface_matches_baseline() =>
        AssertBaseline(typeof(FlowLedger.Application.Abstractions.IDomainEventDispatcher).Assembly);

    /// <summary>
    /// #121: use-case handlers are internal (the Api and Worker see them through InternalsVisibleTo); only Contracts and the
    /// ports stay public. The cap can only be lowered: a new public Application type has to displace an existing one or be argued
    /// in a review that raises it on purpose.
    /// </summary>
    private const int ApplicationPublicTypeCap = 304;

    [Fact]
    public void Application_handlers_are_internal_and_the_public_type_count_stays_capped()
    {
        var assembly = typeof(FlowLedger.Application.Abstractions.IDomainEventDispatcher).Assembly;
        var exported = assembly.GetExportedTypes();

        exported.Where(t => t.IsClass && t.Name.EndsWith("Handler", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .Should().BeEmpty("handlers are internal; hosts reach them through InternalsVisibleTo");
        exported.Length.Should().BeLessThanOrEqualTo(ApplicationPublicTypeCap);
    }

    [Fact]
    public void Infrastructure_public_surface_matches_baseline() =>
        AssertBaseline(typeof(FlowLedger.Infrastructure.Persistence.FlowLedgerDbContext).Assembly);

    // The remaining shippable assemblies (#121): their public surface can only grow through a reviewed baseline diff too.
    [Fact]
    public void Integrations_Abstractions_public_surface_matches_baseline() =>
        AssertBaseline(typeof(FlowLedger.Integrations.Abstractions.ConnectorIds).Assembly);

    [Fact]
    public void Integrations_Mx_public_surface_matches_baseline() =>
        AssertBaseline(typeof(FlowLedger.Integrations.Mx.MxFinancialDataProvider).Assembly);

    [Fact]
    public void Integrations_Simulated_public_surface_matches_baseline() =>
        AssertBaseline(typeof(FlowLedger.Integrations.Simulated.SimulatedFinancialDataProvider).Assembly);

    [Fact]
    public void Integrations_Quiltt_public_surface_matches_baseline() =>
        AssertBaseline(typeof(FlowLedger.Integrations.Quiltt.QuilttFinancialDataProvider).Assembly);

    [Fact]
    public void Integrations_SimpleFin_public_surface_matches_baseline() =>
        AssertBaseline(AssemblyOf("FlowLedger.Integrations.SimpleFin"));

    [Fact]
    public void Web_public_surface_matches_baseline() =>
        AssertBaseline(AssemblyOf("FlowLedger.Web"));

    [Fact]
    public void Worker_public_surface_matches_baseline() =>
        AssertBaseline(AssemblyOf("FlowLedger.Worker"));

    private static Assembly AssemblyOf(string name) => Assembly.Load(name);

    private static void AssertBaseline(Assembly assembly)
    {
        var name = assembly.GetName().Name!;
        var actual = assembly.GetExportedTypes()
            .Select(t => $"{Kind(t)} {t.FullName}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        var path = Path.Combine(FindRepoRoot(), "tests", "FlowLedger.Architecture.Tests", "PublicApi", name + ".txt");

        if (Environment.GetEnvironmentVariable("FLOWLEDGER_UPDATE_PUBLIC_API") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Join("\n", actual) + "\n");
            return;
        }

        Assert.True(File.Exists(path), $"Missing public API baseline {path}. Generate it with FLOWLEDGER_UPDATE_PUBLIC_API=1.");
        var expected = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();

        var added = actual.Except(expected).ToList();
        var removed = expected.Except(actual).ToList();
        Assert.True(added.Count == 0 && removed.Count == 0,
            $"{name} public surface changed. Prefer 'internal' unless another assembly consumes the type; otherwise update the baseline.\n"
            + $"Added ({added.Count}):\n  {string.Join("\n  ", added)}\nRemoved ({removed.Count}):\n  {string.Join("\n  ", removed)}");
    }

    private static string Kind(Type t) =>
        t.IsInterface ? "interface"
        : t.IsEnum ? "enum"
        : typeof(Delegate).IsAssignableFrom(t) ? "delegate"
        : t.IsValueType ? "struct"
        : t.IsAbstract && t.IsSealed ? "static class"
        : t.IsAbstract ? "abstract class"
        : t.IsSealed ? "sealed class"
        : "class";

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
