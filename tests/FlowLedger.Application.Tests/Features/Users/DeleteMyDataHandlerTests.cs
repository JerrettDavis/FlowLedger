using FlowLedger.Application.Features.Users;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features.Users;

/// <summary>#189: owner self-service erasure needs the typed phrase, refuses the household tenant before any provider call, and revokes before it erases.</summary>
public sealed class DeleteMyDataHandlerTests
{
    private static readonly Guid Household = Guid.NewGuid();

    private sealed class Recorder
    {
        public List<string> Calls { get; } = [];
    }

    private sealed class Revocation(Recorder rec, IReadOnlyList<ProviderRevocationResult>? results = null) : ITenantProviderRevocation
    {
        public Task<IReadOnlyList<ProviderRevocationResult>> RevokeAllAsync(Guid tenantId, CancellationToken ct = default)
        {
            rec.Calls.Add("revoke");
            return Task.FromResult(results ?? [new ProviderRevocationResult("MX", ProviderRevocationStatus.Revoked)]);
        }
    }

    private sealed class Erasure(Recorder rec) : ITenantErasure
    {
        public string? RefusalFor(Guid tenantId) => tenantId == Household ? "The household tenant cannot be erased." : null;

        public Task<TenantErasureResult> EraseAsync(Guid tenantId, CancellationToken ct = default)
        {
            rec.Calls.Add("erase");
            return Task.FromResult(new TenantErasureResult(tenantId, new Dictionary<string, int> { ["accounts"] = 2, ["transactions"] = 5 }, 1, DateTimeOffset.UnixEpoch));
        }
    }

    private static DeleteMyDataHandler Handler(Recorder rec, IReadOnlyList<ProviderRevocationResult>? results = null) => new(new Revocation(rec, results), new Erasure(rec));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("delete my data")]
    [InlineData("DELETE")]
    [InlineData("yes")]
    public async Task Anything_but_the_exact_phrase_does_nothing(string? typed)
    {
        var rec = new Recorder();

        var outcome = await Handler(rec).HandleAsync(Guid.NewGuid(), typed);

        outcome.Done.Should().BeFalse();
        outcome.ConfirmationMismatch.Should().BeTrue();
        rec.Calls.Should().BeEmpty("no provider is contacted and nothing is erased without the typed confirmation");
    }

    [Fact]
    public async Task The_phrase_revokes_at_providers_first_then_erases_and_reports_both()
    {
        var rec = new Recorder();
        var tenant = Guid.NewGuid();

        var outcome = await Handler(rec).HandleAsync(tenant, "  DELETE MY DATA ");

        outcome.Done.Should().BeTrue();
        rec.Calls.Should().Equal("revoke", "erase");
        outcome.Erasure!.TenantId.Should().Be(tenant);
        outcome.Erasure.TotalRows.Should().Be(7);
        outcome.Providers.Should().ContainSingle(p => p.Connector == "MX" && p.Status == ProviderRevocationStatus.Revoked);
    }

    [Fact]
    public async Task The_household_tenant_is_refused_before_any_provider_is_told_anything()
    {
        var rec = new Recorder();

        var outcome = await Handler(rec).HandleAsync(Household, DeleteMyDataHandler.ConfirmationPhrase);

        outcome.Done.Should().BeFalse();
        outcome.ConfirmationMismatch.Should().BeFalse();
        outcome.Refusal.Should().Contain("household");
        rec.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_provider_revocation_is_reported_and_does_not_block_the_erasure()
    {
        var rec = new Recorder();
        var failed = new[] { new ProviderRevocationResult("MX", ProviderRevocationStatus.Failed), new ProviderRevocationResult("SimpleFIN", ProviderRevocationStatus.NotSupported) };

        var outcome = await Handler(rec, failed).HandleAsync(Guid.NewGuid(), DeleteMyDataHandler.ConfirmationPhrase);

        outcome.Done.Should().BeTrue();
        outcome.Providers.Select(p => p.Status).Should().Equal(ProviderRevocationStatus.Failed, ProviderRevocationStatus.NotSupported);
        rec.Calls.Should().Equal("revoke", "erase");
    }
}
