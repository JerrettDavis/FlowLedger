namespace FlowLedger.Architecture.Tests;

/// <summary>
/// Architecture dependency-rule tests (ArchUnitNET or NetArchTest added in Milestone 1).
/// Milestone 0 smoke: verify the three key assemblies load and are distinct.
/// </summary>
public sealed class DependencyRuleTests
{
    [Fact]
    public void Domain_does_not_reference_Infrastructure()
    {
        var domainAsm = System.Reflection.Assembly.Load("FlowLedger.Domain");
        const string infraName = "FlowLedger.Infrastructure";

        var domainRefs = domainAsm.GetReferencedAssemblies()
            .Select(n => n.Name ?? string.Empty);

        Assert.DoesNotContain(domainRefs,
            r => r.Equals(infraName, StringComparison.Ordinal));
    }

    [Fact]
    public void Application_does_not_reference_Infrastructure()
    {
        var appAsm = System.Reflection.Assembly.Load("FlowLedger.Application");
        const string infraName = "FlowLedger.Infrastructure";

        var appRefs = appAsm.GetReferencedAssemblies()
            .Select(n => n.Name ?? string.Empty);

        Assert.DoesNotContain(appRefs,
            r => r.Equals(infraName, StringComparison.Ordinal));
    }
}
