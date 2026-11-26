using System.Diagnostics.Metrics;
using System.Text.Encodings.Web;
using FlowLedger.Api.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowLedger.Api.Tests.Security;

/// <summary>Pure unit tests (no host / database) for the API key strength guard and the rotation (PreviousKey) path.</summary>
public sealed class ApiKeyPolicyTests
{
    private static readonly string Strong = new string('a', 8) + "0123456789abcdef0123456789abcdef"; // 40 chars

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("0123456789012345678901234567890")] // 31 chars
    public void Short_keys_are_rejected(string key) =>
        ApiKeyPolicy.ValidateKey(key, "Api:Key").Should().NotBeNull();

    [Theory]
    [InlineData("changeme-changeme-changeme-changeme-1234")]
    [InlineData("PASSWORD-password-password-password-1234")]
    [InlineData("dev-local-key-not-for-production")]
    [InlineData("ci-test-api-key-not-for-production")]
    public void Denylisted_keys_are_rejected_even_when_long_enough(string key) =>
        ApiKeyPolicy.ValidateKey(key, "Api:Key").Should().NotBeNull();

    [Fact]
    public void Sixty_four_hex_key_passes() =>
        ApiKeyPolicy.ValidateKey(Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), "Api:Key")
            .Should().BeNull();

    [Fact]
    public void Integration_test_key_passes_so_production_factories_boot() =>
        ApiKeyPolicy.ValidateKey("integration-test-key-not-for-production", "Api:Key").Should().BeNull();

    [Fact]
    public void Previous_key_is_optional_but_must_be_strong_and_distinct()
    {
        ApiKeyPolicy.ValidateOptions(new ApiOptions { Key = Strong, PreviousKey = null }).Should().BeNull();
        ApiKeyPolicy.ValidateOptions(new ApiOptions { Key = Strong, PreviousKey = "weak" }).Should().NotBeNull();
        ApiKeyPolicy.ValidateOptions(new ApiOptions { Key = Strong, PreviousKey = Strong }).Should().NotBeNull();
        ApiKeyPolicy.ValidateOptions(new ApiOptions { Key = Strong, PreviousKey = new string('b', 40) }).Should().BeNull();
    }

    [Fact]
    public void Match_accepts_previous_key_only_while_it_is_configured()
    {
        var old = new string('b', 40);

        ApiKeyPolicy.Match(old, new ApiOptions { Key = Strong, PreviousKey = old }).Should().Be(ApiKeyMatch.Previous);
        ApiKeyPolicy.Match(old, new ApiOptions { Key = Strong }).Should().Be(ApiKeyMatch.None);
        ApiKeyPolicy.Match(Strong, new ApiOptions { Key = Strong, PreviousKey = old }).Should().Be(ApiKeyMatch.Current);
        ApiKeyPolicy.Match("x", new ApiOptions { Key = Strong, PreviousKey = old }).Should().Be(ApiKeyMatch.None);
        ApiKeyPolicy.Match(string.Empty, new ApiOptions { Key = Strong, PreviousKey = string.Empty }).Should().Be(ApiKeyMatch.None);
    }

    [Fact]
    public async Task Handler_authenticates_previous_key_and_counts_failures()
    {
        var old = new string('b', 40);
        var opts = new ApiOptions { Key = Strong, PreviousKey = old };

        var failures = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Meter.Name == ApiKeyAuthenticationHandler.MeterName && i.Name == ApiKeyAuthenticationHandler.FailureCounterName)
            {
                l.EnableMeasurementEvents(i);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, m, _, _) => Interlocked.Add(ref failures, m));
        listener.Start();

        (await Authenticate(opts, old)).Succeeded.Should().BeTrue("PreviousKey is accepted during rotation");
        (await Authenticate(new ApiOptions { Key = Strong }, old)).Succeeded.Should().BeFalse("old key is dead once PreviousKey is unset");
        (await Authenticate(opts, "wrong-key-value")).Succeeded.Should().BeFalse();

        failures.Should().Be(2);
    }

    private static async Task<AuthenticateResult> Authenticate(ApiOptions opts, string key)
    {
        var handler = new ApiKeyAuthenticationHandler(
            new StaticMonitor(new AuthenticationSchemeOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            new StaticSnapshot(opts));
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-Api-Key"] = key;
        await handler.InitializeAsync(new AuthenticationScheme(ApiKeyAuthenticationHandler.SchemeName, null, typeof(ApiKeyAuthenticationHandler)), ctx);
        return await handler.AuthenticateAsync();
    }

    private sealed class StaticMonitor(AuthenticationSchemeOptions v) : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue => v;
        public AuthenticationSchemeOptions Get(string? name) => v;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    private sealed class StaticSnapshot(ApiOptions v) : IOptionsSnapshot<ApiOptions>
    {
        public ApiOptions Value => v;
        public ApiOptions Get(string? name) => v;
    }
}
