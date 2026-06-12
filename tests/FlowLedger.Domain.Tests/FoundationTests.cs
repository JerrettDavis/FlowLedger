using FlowLedger.SharedKernel;

namespace FlowLedger.Domain.Tests;

/// <summary>Milestone 0 smoke tests preserved — verifies SharedKernel abstractions.</summary>
public sealed class FoundationTests
{
    [Fact]
    public void SharedKernel_ITenantContext_interface_exists()
    {
        var type = typeof(ITenantContext);
        Assert.NotNull(type);
        Assert.True(type.IsInterface);
    }

    [Fact]
    public void SharedKernel_IObjectStorage_interface_exists()
    {
        var type = typeof(IObjectStorage);
        Assert.NotNull(type);
        Assert.True(type.IsInterface);
    }

    [Fact]
    public void Domain_assembly_loads()
    {
        var asm = System.Reflection.Assembly.Load("FlowLedger.Domain");
        Assert.NotNull(asm);
    }

    [Fact]
    public void SharedKernel_IDomainEvent_interface_exists()
    {
        var type = typeof(IDomainEvent);
        Assert.True(type.IsInterface);
    }
}
