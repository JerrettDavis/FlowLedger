using FlowLedger.Web.ApiClient;
using FluentAssertions;

namespace FlowLedger.Web.Tests.ApiClient;

public sealed class TenantAssertionSignerTests
{
    [Fact]
    public void Matches_the_golden_vector_the_API_verifier_checks()
        => TenantAssertionSigner.Sign("test-signing-key", Guid.Parse("11111111-2222-3333-4444-555555555555"), new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero))
            .Should().Be("1795003200.b246c9a5dc891a3e72c9a0c8a72bacac676ebb24907efe88406b7b42286cb399");
}
