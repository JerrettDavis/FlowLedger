using System.Text;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx;

namespace FlowLedger.Integrations.Tests.Mx;

/// <summary>
/// Unit tests for <see cref="MxWebhookVerifier"/> — HMAC-SHA256 verification with constant-time
/// comparison. No HTTP.
/// </summary>
public sealed class MxWebhookVerifierTests
{
    private const string Secret = "unit-test-webhook-secret";

    private static readonly byte[] Body =
        Encoding.UTF8.GetBytes("""{"type":"AGGREGATION","member_guid":"MBR-1"}""");

    [Fact]
    public void Verify_accepts_valid_signature()
    {
        var verifier = new MxWebhookVerifier(Secret);
        var signature = verifier.ComputeSignature(Body);

        var act = () => verifier.Verify(Body, signature);

        act.Should().NotThrow();
    }

    [Fact]
    public void Verify_rejects_tampered_body()
    {
        var verifier = new MxWebhookVerifier(Secret);
        var signature = verifier.ComputeSignature(Body);
        var tamperedBody = Encoding.UTF8.GetBytes("""{"type":"AGGREGATION","member_guid":"MBR-EVIL"}""");

        var act = () => verifier.Verify(tamperedBody, signature);

        act.Should().Throw<InvalidWebhookSignatureException>();
    }

    [Fact]
    public void Verify_rejects_tampered_signature()
    {
        var verifier = new MxWebhookVerifier(Secret);

        var act = () => verifier.Verify(Body, "deadbeef" + new string('0', 56));

        act.Should().Throw<InvalidWebhookSignatureException>();
    }

    [Fact]
    public void Verify_rejects_empty_signature()
    {
        var verifier = new MxWebhookVerifier(Secret);

        var act = () => verifier.Verify(Body, string.Empty);

        act.Should().Throw<InvalidWebhookSignatureException>();
    }

    [Fact]
    public void Verify_rejects_null_signature()
    {
        var verifier = new MxWebhookVerifier(Secret);

        var act = () => verifier.Verify(Body, null);

        act.Should().Throw<InvalidWebhookSignatureException>();
    }

    [Fact]
    public void Signature_differs_with_different_secret()
    {
        var a = new MxWebhookVerifier("secret-a").ComputeSignature(Body);
        var b = new MxWebhookVerifier("secret-b").ComputeSignature(Body);

        a.Should().NotBe(b);
    }

    [Fact]
    public void Constructor_rejects_empty_secret()
    {
        var act = () => new MxWebhookVerifier(" ");

        act.Should().Throw<ArgumentException>();
    }
}
