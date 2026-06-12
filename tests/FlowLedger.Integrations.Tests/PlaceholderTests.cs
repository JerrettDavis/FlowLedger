namespace FlowLedger.Integrations.Tests;

/// <summary>
/// Milestone 0 smoke — Integrations tests arrive in Milestone 2.
/// </summary>
public sealed class IntegrationsPlaceholderTests
{
    [Fact]
    public void Integrations_abstractions_assembly_loads()
    {
        var asm = System.Reflection.Assembly.Load("FlowLedger.Integrations.Abstractions");
        Assert.NotNull(asm);
    }
}
