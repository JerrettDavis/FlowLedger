using System.Reflection;
using NetArchTest.Rules;

namespace FlowLedger.Architecture.Tests;

/// <summary>Layering rules for the host projects (API endpoints and the Blazor web front end).</summary>
public sealed class HostLayerDependencyTests
{
    private static readonly Assembly Web = Assembly.Load("FlowLedger.Web");
    private static readonly Assembly Api = Assembly.Load("FlowLedger.Api");

    /// <summary>
    /// Endpoints that still reach into Infrastructure directly. Each is a known debt, not a pattern to copy:
    /// remove an entry once its endpoint goes through an Application port. Adding to this list needs a reason.
    /// </summary>
    private static readonly HashSet<string> EndpointInfrastructureAllowlist = new(StringComparer.Ordinal)
    {
        // Backup health/run, export/import zip: BackupRunner, BackupRunStore, BackupSettings, SnapshotImport, NpgsqlProviderCallLedger.
        "FlowLedger.Api.Endpoints.DataEndpoints",
        // Verified data environment: DataEnvironmentState / DataEnvironment.
        "FlowLedger.Api.Endpoints.EnvironmentEndpoints",
        // Demo connect is refused in production: DataEnvironmentState / DataEnvironment.
        "FlowLedger.Api.Endpoints.SyncEndpoints",
    };

    [Theory]
    [InlineData("FlowLedger.Application")]
    [InlineData("FlowLedger.Infrastructure")]
    public void Web_must_not_depend_on(string forbidden)
    {
        var result = Types.InAssembly(Web).Should().NotHaveDependencyOn(forbidden).GetResult();

        Assert.True(result.IsSuccessful,
            $"Web talks to the API over HTTP and must not reference {forbidden}. Failing types: {string.Join(", ", result.FailingTypeNames ?? [])}");
        Assert.DoesNotContain(Web.GetReferencedAssemblies(), a => a.Name == forbidden);
    }

    [Fact]
    public void Api_endpoints_must_not_depend_on_Infrastructure_outside_the_allowlist()
    {
        var result = Types.InAssembly(Api)
            .That().ResideInNamespace("FlowLedger.Api.Endpoints")
            .ShouldNot().HaveDependencyOn("FlowLedger.Infrastructure")
            .GetResult();

        // Nested/compiler-generated types (closures, records) report as Outer+Inner or Outer/<>c; attribute them to the outer type.
        var offenders = (result.FailingTypeNames ?? [])
            .Select(n => n.Split('+', '/')[0])
            .Distinct(StringComparer.Ordinal)
            .Where(n => !EndpointInfrastructureAllowlist.Contains(n))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Endpoints must depend on Application ports, not Infrastructure. New violations: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Endpoint_allowlist_has_no_stale_entries()
    {
        var failing = (Types.InAssembly(Api)
                .That().ResideInNamespace("FlowLedger.Api.Endpoints")
                .ShouldNot().HaveDependencyOn("FlowLedger.Infrastructure")
                .GetResult().FailingTypeNames ?? [])
            .Select(n => n.Split('+', '/')[0])
            .ToHashSet(StringComparer.Ordinal);

        var stale = EndpointInfrastructureAllowlist.Where(n => !failing.Contains(n)).ToList();

        Assert.True(stale.Count == 0, $"These endpoints no longer use Infrastructure; remove them from the allowlist: {string.Join(", ", stale)}");
    }
}
