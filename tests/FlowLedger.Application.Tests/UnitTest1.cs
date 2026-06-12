namespace FlowLedger.Application.Tests;

/// <summary>
/// Milestone 0 smoke — Application vertical slice tests arrive in Milestone 2.
/// </summary>
public sealed class ApplicationLayerTests
{
    [Fact]
    public void Application_assembly_loads()
    {
        var asm = System.Reflection.Assembly.Load("FlowLedger.Application");
        Assert.NotNull(asm);
    }
}
