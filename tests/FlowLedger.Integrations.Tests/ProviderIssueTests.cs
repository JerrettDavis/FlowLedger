using FlowLedger.Integrations.Abstractions;
using FluentAssertions;

namespace FlowLedger.Integrations.Tests;

public sealed class ProviderIssueTests
{
    [Theory]
    [InlineData(ProviderIssueKind.ReauthRequired, ConnectionStatus.NeedsUserAction, false, true)]
    [InlineData(ProviderIssueKind.InvalidCredentials, ConnectionStatus.NeedsUserAction, false, true)]
    [InlineData(ProviderIssueKind.Disconnected, ConnectionStatus.NeedsUserAction, false, true)]
    [InlineData(ProviderIssueKind.InstitutionUnavailable, ConnectionStatus.Degraded, true, false)]
    [InlineData(ProviderIssueKind.ProviderUnavailable, ConnectionStatus.Degraded, true, false)]
    [InlineData(ProviderIssueKind.ServiceUnavailable, ConnectionStatus.Degraded, true, false)]
    [InlineData(ProviderIssueKind.RateLimited, ConnectionStatus.Degraded, true, false)]
    [InlineData(ProviderIssueKind.Internal, ConnectionStatus.Error, true, false)]
    [InlineData(ProviderIssueKind.Unknown, ConnectionStatus.Error, false, false)]
    public void Of_AppliesKindDefaults(ProviderIssueKind kind, ConnectionStatus status, bool retryable, bool userAction)
    {
        var issue = ProviderIssue.Of(kind, "m");

        issue.Retryable.Should().Be(retryable);
        issue.RequiresUserAction.Should().Be(userAction);
        issue.ToConnectionStatus().Should().Be(status);
    }

    [Fact]
    public void FromException_UsesTypeDefaults_AndRetryAfter()
    {
        ProviderIssue.FromException(new NeedsUserActionProviderException("x")).Kind.Should().Be(ProviderIssueKind.ReauthRequired);
        ProviderIssue.FromException(new FatalProviderException("x")).Kind.Should().Be(ProviderIssueKind.Internal);
        ProviderIssue.FromException(new TransientProviderException("x", TimeSpan.FromMinutes(1))).RetryAfter.Should().NotBeNull();

        var limited = ProviderIssue.FromException(new RateLimitedProviderException("x", DateTimeOffset.UtcNow.AddSeconds(30)));
        limited.Kind.Should().Be(ProviderIssueKind.RateLimited);
        limited.RetryAfter.Should().NotBeNull();

        ProviderIssue.FromException(new InvalidOperationException("boom")).Kind.Should().Be(ProviderIssueKind.Unknown);
    }

    [Fact]
    public void ConnectorCanRefineTheKind_WithoutChangingTheExceptionType()
    {
        var ex = new TransientProviderException("bank down") { Kind = ProviderIssueKind.InstitutionUnavailable };

        ProviderIssue.FromException(ex).Kind.Should().Be(ProviderIssueKind.InstitutionUnavailable);
        new FatalProviderException("bad creds") { Kind = ProviderIssueKind.InvalidCredentials }.Kind
            .Should().Be(ProviderIssueKind.InvalidCredentials);
    }

    private sealed class StubProvider(ConnectionStatus status) : IFinancialDataProvider
    {
        public string ProviderName => "Stub";
        public ProviderCapabilities Capabilities => ProviderCapabilities.None;
        public Task<ProviderMemberRef> BeginConnectionAsync(FlowLedger.Domain.ValueObjects.TenantId t, CancellationToken c = default) => throw new NotSupportedException();
        public Task<ConnectionStatus> GetConnectionStatusAsync(string m, CancellationToken c = default) => Task.FromResult(status);
        public Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(string m, CancellationToken c = default) => throw new NotSupportedException();
        public Task<TransactionPage> GetTransactionsAsync(string a, SyncCursor cur, int p = 100, CancellationToken c = default) => throw new NotSupportedException();
        public Task VerifyWebhookAsync(ReadOnlyMemory<byte> b, string s, CancellationToken c = default) => throw new NotSupportedException();
        public Task<ProviderWebhookEvent> ParseWebhookAsync(ReadOnlyMemory<byte> b, CancellationToken c = default) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(ConnectionStatus.Healthy, ProviderIssueKind.None)]
    [InlineData(ConnectionStatus.NeedsUserAction, ProviderIssueKind.ReauthRequired)]
    [InlineData(ConnectionStatus.Degraded, ProviderIssueKind.InstitutionUnavailable)]
    [InlineData(ConnectionStatus.Error, ProviderIssueKind.Unknown)]
    public async Task DefaultHealth_DerivesFromStatus_ForConnectorsThatDoNotOverride(ConnectionStatus status, ProviderIssueKind expected)
    {
        IFinancialDataProvider p = new StubProvider(status);

        var health = await p.GetConnectionHealthAsync("m");

        health.Status.Should().Be(status);
        (health.Issue?.Kind ?? ProviderIssueKind.None).Should().Be(expected);
    }
}
