using FlowLedger.Infrastructure.DataSafety;
using FluentAssertions;
using Xunit;

namespace FlowLedger.Infrastructure.Tests.Persistence;

public sealed class DataEnvironmentSimpleFinTests
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "sandbox")]
    public void SimpleFin_always_means_production(bool mxEnabled, string? mxEnv) =>
        DataEnvironmentGuard.Expected(mxEnabled, mxEnv, simpleFinEnabled: true).Should().Be(DataEnvironment.Production);

    [Fact]
    public void Without_simplefin_behaviour_is_unchanged() =>
        DataEnvironmentGuard.Expected(false, null).Should().Be(DataEnvironment.Demo);
}
