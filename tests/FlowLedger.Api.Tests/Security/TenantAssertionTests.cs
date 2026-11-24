using FlowLedger.Api.Tenancy;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Security;

public sealed class TenantAssertionTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Valid_signature_verifies()
        => TenantAssertion.Verify("k", Tenant, TenantAssertion.Sign("k", Tenant, Now), Now.AddSeconds(30)).Should().BeTrue();

    [Fact]
    public void Wrong_key_other_tenant_expired_or_garbage_are_rejected()
    {
        var sig = TenantAssertion.Sign("k", Tenant, Now);
        TenantAssertion.Verify("other", Tenant, sig, Now).Should().BeFalse();
        TenantAssertion.Verify("k", Guid.NewGuid(), sig, Now).Should().BeFalse();
        TenantAssertion.Verify("k", Tenant, sig, Now.AddMinutes(6)).Should().BeFalse();
        TenantAssertion.Verify("k", Tenant, "nonsense", Now).Should().BeFalse();
        TenantAssertion.Verify("k", Tenant, null, Now).Should().BeFalse();
        TenantAssertion.Verify("", Tenant, sig, Now).Should().BeFalse();
    }

    [Fact]
    public void Matches_the_golden_vector_the_Web_signer_also_checks()
        => TenantAssertion.Sign("test-signing-key", Tenant, Now)
            .Should().Be("1795003200.b246c9a5dc891a3e72c9a0c8a72bacac676ebb24907efe88406b7b42286cb399");
}
