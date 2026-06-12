namespace FlowLedger.Infrastructure.Tests;

/// <summary>
/// Milestone 0 smoke — EF Core + Testcontainers integration tests arrive in Milestone 2.
/// </summary>
public sealed class InfrastructurePlaceholderTests
{
    [Fact]
    public void Infrastructure_assembly_loads()
    {
        var asm = System.Reflection.Assembly.Load("FlowLedger.Infrastructure");
        Assert.NotNull(asm);
    }
}
