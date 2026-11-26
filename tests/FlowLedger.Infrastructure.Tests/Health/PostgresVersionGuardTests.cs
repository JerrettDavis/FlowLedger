using FlowLedger.Infrastructure.DataSafety;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Health;

public sealed class PostgresVersionGuardTests
{
    [Fact]
    public void Tested_major_has_no_warning() =>
        PostgresVersionGuard.Evaluate(170004).Should().BeNull();

    [Theory]
    [InlineData(160009)]
    [InlineData(180001)]
    public void Other_supported_majors_warn_but_start(int versionNum) =>
        PostgresVersionGuard.Evaluate(versionNum).Should().Contain("not the tested version");

    [Theory]
    [InlineData(150014)]
    [InlineData(90624)]
    public void Old_majors_fail_with_clear_message(int versionNum)
    {
        var act = () => PostgresVersionGuard.Evaluate(versionNum);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Unsupported PostgreSQL server version*requires PostgreSQL 16*");
    }
}
