using NetArchTest.Rules;

namespace FlowLedger.Architecture.Tests;

/// <summary>
/// Clean-architecture layering rules verified by NetArchTest.Rules.
/// Each rule asserts no forbidden dependencies between layers.
/// Rules use type-based assembly loading (vs string-based Assembly.Load)
/// and detect transitive violations, not just direct references.
/// </summary>
public sealed class LayerDependencyTests
{
    private static class Assemblies
    {
        public static readonly System.Reflection.Assembly Domain =
            typeof(FlowLedger.Domain.Aggregates.Account).Assembly;

        public static readonly System.Reflection.Assembly Application =
            typeof(FlowLedger.Application.Abstractions.IDomainEventDispatcher).Assembly;

        public static readonly System.Reflection.Assembly Infrastructure =
            typeof(FlowLedger.Infrastructure.Persistence.FlowLedgerDbContext).Assembly;

        public static readonly System.Reflection.Assembly IntegrationAbstractions =
            typeof(FlowLedger.Integrations.Abstractions.IFinancialDataProvider).Assembly;

        public static readonly System.Reflection.Assembly IntegrationSimulated =
            typeof(FlowLedger.Integrations.Simulated.SimulatedFinancialDataProvider).Assembly;

        public static readonly System.Reflection.Assembly IntegrationMx =
            System.Reflection.Assembly.Load("FlowLedger.Integrations.Mx");
    }

    [Fact]
    public void Domain_must_not_depend_on_Application()
    {
        var result = Types
            .InAssembly(Assemblies.Domain)
            .Should()
            .NotHaveDependencyOnAny("FlowLedger.Application")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Domain should not reference Application. Failing types: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Domain_must_not_depend_on_Infrastructure()
    {
        var result = Types
            .InAssembly(Assemblies.Domain)
            .Should()
            .NotHaveDependencyOnAny("FlowLedger.Infrastructure")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Domain should not reference Infrastructure. Failing types: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Domain_must_not_depend_on_EntityFrameworkCore()
    {
        var result = Types
            .InAssembly(Assemblies.Domain)
            .Should()
            .NotHaveDependencyOnAny("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Domain should not reference EntityFrameworkCore. Failing types: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Domain_must_not_depend_on_IntegrationAbstractions()
    {
        var result = Types
            .InAssembly(Assemblies.Domain)
            .Should()
            .NotHaveDependencyOnAny("FlowLedger.Integrations.Abstractions")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Domain should not reference Integrations.Abstractions. Failing types: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Domain_must_not_depend_on_IntegrationSimulated()
    {
        var result = Types
            .InAssembly(Assemblies.Domain)
            .Should()
            .NotHaveDependencyOnAny("FlowLedger.Integrations.Simulated")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Domain should not reference Integrations.Simulated. Failing types: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Domain_must_not_depend_on_IntegrationMx()
    {
        var result = Types
            .InAssembly(Assemblies.Domain)
            .Should()
            .NotHaveDependencyOnAny("FlowLedger.Integrations.Mx")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Domain should not reference Integrations.Mx. Failing types: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Application_must_not_depend_on_Infrastructure()
    {
        var result = Types
            .InAssembly(Assemblies.Application)
            .Should()
            .NotHaveDependencyOnAny("FlowLedger.Infrastructure")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Application should not reference Infrastructure. Failing types: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Application_must_not_depend_on_EntityFrameworkCore()
    {
        var result = Types
            .InAssembly(Assemblies.Application)
            .Should()
            .NotHaveDependencyOnAny("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Application should not reference EntityFrameworkCore. Failing types: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void IntegrationAbstractions_must_not_depend_on_Infrastructure()
    {
        var result = Types
            .InAssembly(Assemblies.IntegrationAbstractions)
            .Should()
            .NotHaveDependencyOnAny("FlowLedger.Infrastructure")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Integrations.Abstractions should not reference Infrastructure. Failing types: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void IntegrationAbstractions_must_not_depend_on_IntegrationSimulated()
    {
        var result = Types
            .InAssembly(Assemblies.IntegrationAbstractions)
            .Should()
            .NotHaveDependencyOnAny("FlowLedger.Integrations.Simulated")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Integrations.Abstractions should not reference Integrations.Simulated. Failing types: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void IntegrationAbstractions_must_not_depend_on_IntegrationMx()
    {
        var result = Types
            .InAssembly(Assemblies.IntegrationAbstractions)
            .Should()
            .NotHaveDependencyOnAny("FlowLedger.Integrations.Mx")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Integrations.Abstractions should not reference Integrations.Mx. Failing types: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void No_scaffold_types_remain()
    {
        var scaffoldTypeNames = new[] { "Class1", "UnitTest1", "WeatherForecast", "Counter" };

        var allAssemblies = new[]
        {
            Assemblies.Domain,
            Assemblies.Application,
            Assemblies.Infrastructure,
            Assemblies.IntegrationAbstractions,
            Assemblies.IntegrationSimulated,
            Assemblies.IntegrationMx
        };

        foreach (var assemblyName in scaffoldTypeNames)
        {
            foreach (var assembly in allAssemblies)
            {
                var scaffoldTypes = Types
                    .InAssembly(assembly)
                    .That()
                    .HaveName(assemblyName)
                    .GetTypes();

                Assert.False(
                    scaffoldTypes.Any(),
                    $"Scaffold type '{assemblyName}' should not exist in assembly {assembly.GetName().Name}. Found types: {string.Join(", ", scaffoldTypes.Select(t => t.FullName))}");
            }
        }
    }
}
