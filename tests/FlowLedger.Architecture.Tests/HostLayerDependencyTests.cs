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
        // Backup health/run, export/import zip: BackupRunner, BackupRunStore, BackupSettings, SnapshotImport, ProviderCallLedger.
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

    /// <summary>
    /// Non-endpoint Api types that use Infrastructure. Program and MigrateCommand are the composition root; the other two are
    /// known debt (the audit sink and the on-demand sync job should sit behind Application ports).
    /// </summary>
    private static readonly HashSet<string> ApiHostInfrastructureAllowlist = new(StringComparer.Ordinal)
    {
        "Program",
        "FlowLedger.Api.MigrateCommand",
        "FlowLedger.Api.Middleware.AuditMiddleware",
        "FlowLedger.Api.Jobs.OnDemandSyncJob",
    };

    /// <summary>Api types that name a concrete connector assembly: only the Quiltt endpoint (session minting) is not composition.</summary>
    private static readonly HashSet<string> ApiConcreteConnectorAllowlist = new(StringComparer.Ordinal)
    {
        "FlowLedger.Api.Endpoints.QuilttEndpoints",
    };

    private static readonly string[] ConcreteConnectors =
    [
        "FlowLedger.Integrations.Mx", "FlowLedger.Integrations.Quiltt", "FlowLedger.Integrations.SimpleFin", "FlowLedger.Integrations.Simulated",
    ];

    private static HashSet<string> Outer(IEnumerable<string>? names) =>
        (names ?? []).Select(n => n.Split('+', '/')[0]).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Api_host_code_outside_endpoints_uses_Infrastructure_only_in_the_composition_root_and_allowlist()
    {
        var failing = Outer(Types.InAssembly(Api).That().DoNotResideInNamespace("FlowLedger.Api.Endpoints")
            .ShouldNot().HaveDependencyOn("FlowLedger.Infrastructure").GetResult().FailingTypeNames);

        var fresh = failing.Where(n => !ApiHostInfrastructureAllowlist.Contains(n)).ToList();
        var stale = ApiHostInfrastructureAllowlist.Where(n => !failing.Contains(n)).ToList();

        Assert.True(fresh.Count == 0, $"Api host types must use Application ports, not Infrastructure. New violations: {string.Join(", ", fresh)}");
        Assert.True(stale.Count == 0, $"These types no longer use Infrastructure; remove them from the allowlist: {string.Join(", ", stale)}");
    }

    [Fact]
    public void Api_does_not_name_concrete_connector_assemblies_outside_the_allowlist()
    {
        var failing = Outer(Types.InAssembly(Api).ShouldNot().HaveDependencyOnAny(ConcreteConnectors).GetResult().FailingTypeNames);

        var fresh = failing.Where(n => !ApiConcreteConnectorAllowlist.Contains(n)).ToList();
        var stale = ApiConcreteConnectorAllowlist.Where(n => !failing.Contains(n)).ToList();

        Assert.True(fresh.Count == 0, $"Api code must go through IFinancialDataProvider/IConnectorRegistry. New violations: {string.Join(", ", fresh)}");
        Assert.True(stale.Count == 0, $"These types no longer name a connector assembly; remove them from the allowlist: {string.Join(", ", stale)}");
    }

    [Fact]
    public void Worker_does_not_name_concrete_connector_assemblies()
    {
        var worker = Assembly.Load("FlowLedger.Worker");
        var result = Types.InAssembly(worker).ShouldNot().HaveDependencyOnAny(ConcreteConnectors).GetResult();

        Assert.True(result.IsSuccessful, $"Worker must resolve connectors through Infrastructure composition. Failing types: {string.Join(", ", result.FailingTypeNames ?? [])}");
        Assert.DoesNotContain(worker.GetReferencedAssemblies(), a => a.Name is not null && ConcreteConnectors.Contains(a.Name));
    }
}
