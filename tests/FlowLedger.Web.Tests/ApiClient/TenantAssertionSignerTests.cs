using FlowLedger.Web.ApiClient;
using FluentAssertions;

namespace FlowLedger.Web.Tests.ApiClient;

public sealed class TenantAssertionSignerTests
{
    [Fact]
    public void Matches_the_golden_vector_the_API_verifier_checks()
        => TenantAssertionSigner.Sign("test-signing-key", Guid.Parse("11111111-2222-3333-4444-555555555555"), new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero))
            .Should().Be("1795003200.b246c9a5dc891a3e72c9a0c8a72bacac676ebb24907efe88406b7b42286cb399");

    [Fact]
    public void V2_matches_the_golden_vector_the_API_verifier_checks()
        => TenantAssertionSigner.SignV2(
                "test-signing-key", Guid.Parse("11111111-2222-3333-4444-555555555555"), new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero),
                "post", "/api/accounts", "000102030405060708090a0b0c0d0e0f")
            .Should().Be("v2.1795003200.000102030405060708090a0b0c0d0e0f.61d03c130037c0e18e14b229d0309d627b5adb740ea27982f29fd8c1a4d824e0");

    [Fact]
    public void V2_uses_a_fresh_nonce_per_signature()
    {
        var tenant = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

        TenantAssertionSigner.SignV2("k", tenant, now, "GET", "/api/x").Should().NotBe(TenantAssertionSigner.SignV2("k", tenant, now, "GET", "/api/x"));
    }

    [Fact]
    public void Path_is_decoded_and_the_query_is_not_signed()
        => TenantAssertionSigner.PathOf(new Uri("http://api/api/payees/a%20b?x=1")).Should().Be("/api/payees/a b");
}
