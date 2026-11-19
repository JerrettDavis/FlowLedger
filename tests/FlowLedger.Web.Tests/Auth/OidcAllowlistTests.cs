using FlowLedger.Web.Auth;
using FluentAssertions;

namespace FlowLedger.Web.Tests.Auth;

public sealed class OidcAllowlistTests
{
    [Fact]
    public void NoAllowlist_AllowsAnyone() =>
        new OidcOptions().IsEmailAllowed(null, false).Should().BeTrue();

    [Theory]
    [InlineData("a@x.com", true, true)]
    [InlineData("A@X.COM", true, true)]
    [InlineData("a@x.com", false, false)]
    [InlineData("b@x.com", true, false)]
    [InlineData(null, true, false)]
    public void ExactEmail_RequiresVerifiedMatch(string? email, bool verified, bool expected) =>
        new OidcOptions { AllowedEmails = ["a@x.com"] }.IsEmailAllowed(email, verified).Should().Be(expected);

    [Theory]
    [InlineData("anyone@family.org", true, true)]
    [InlineData("anyone@evil.family.org", true, false)]
    [InlineData("anyone@family.org.evil.com", true, false)]
    [InlineData("anyone@family.org", false, false)]
    public void Domain_MatchesExactDomainOnly(string email, bool verified, bool expected) =>
        new OidcOptions { AllowedEmailDomains = ["@family.org"] }.IsEmailAllowed(email, verified).Should().Be(expected);
}
