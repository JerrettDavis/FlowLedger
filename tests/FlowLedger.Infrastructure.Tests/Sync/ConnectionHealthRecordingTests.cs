using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Simulated;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLedger.Infrastructure.Tests.Sync;

/// <summary>
/// ADR 0018: the sync service classifies, persists and rethrows provider failures identically for EVERY connector.
/// The provider here is a vendor-neutral fake that can throw every <see cref="ProviderIssueKind"/>; nothing below knows
/// a real connector's name or native status strings.
/// </summary>
[Collection("Integration")]
public sealed class ConnectionHealthRecordingTests : IAsyncLifetime
{
    private const string Vendor = "FakeVendor";
    private static readonly ProviderIssueKind[] IssueKinds =
        Enum.GetValues<ProviderIssueKind>().Where(k => k != ProviderIssueKind.None).ToArray();

    private readonly IntegrationTestFixture _fixture;

    public ConnectionHealthRecordingTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static Exception ExceptionFor(ProviderIssueKind kind) => kind switch
    {
        ProviderIssueKind.RateLimited => new RateLimitedProviderException("throttled", DateTimeOffset.UtcNow.AddMinutes(30)) { Kind = kind },
        ProviderIssueKind.ReauthRequired or ProviderIssueKind.InvalidCredentials or ProviderIssueKind.Disconnected
            => new NeedsUserActionProviderException("user must act", "hint") { Kind = kind },
        ProviderIssueKind.Internal or ProviderIssueKind.Unknown => new FatalProviderException("broke") { Kind = kind },
        _ => new TransientProviderException("down", TimeSpan.FromMinutes(5)) { Kind = kind },
    };

    private async Task<(FlowLedgerDbContext Db, FinancialSyncService Sync, EfProviderConnectionStore Store, FakeProvider Provider)> ArrangeAsync(
        TestTenantContext tenant, IProviderConnectionStore? healthOverride = null)
    {
        var db = _fixture.CreateDbContext(tenant);
        var store = new EfProviderConnectionStore(db, tenant);
        await store.SaveAsync(Vendor, "member-1", "Fake Bank", "Active"); // connection exists, so unattended syncs never provision
        var provider = new FakeProvider();
        var sync = new FinancialSyncService(
            provider, db, tenant, new EfSyncCursorStore(db, tenant), NullLogger<FinancialSyncService>.Instance,
            health: healthOverride ?? store);
        return (db, sync, store, provider);
    }

    [DockerFact]
    public async Task Every_issue_kind_is_persisted_the_same_way_and_the_original_exception_is_rethrown()
    {
        foreach (var kind in IssueKinds)
        {
            var tenant = TestTenantContext.New();
            var (db, sync, store, provider) = await ArrangeAsync(tenant);
            await using var _ = db;
            var thrown = ExceptionFor(kind);
            provider.Throw = thrown;

            var act = () => sync.SyncAsync();

            (await act.Should().ThrowAsync<ProviderException>()).Which.Should().BeSameAs(thrown, $"{kind} must never be swallowed or wrapped");

            var issue = (await store.ListAsync()).Single().Issue;
            issue.Should().NotBeNull($"{kind} must be recorded");
            issue!.Kind.Should().Be(kind.ToString());
            issue.Retryable.Should().Be(ProviderIssue.IsRetryableByDefault(kind), kind.ToString());
            issue.RequiresUserAction.Should().Be(ProviderIssue.RequiresUserByDefault(kind), kind.ToString());
            issue.ConsecutiveFailures.Should().Be(1);
            issue.Message.Should().NotBeNullOrEmpty();
        }
    }

    [DockerFact]
    public async Task Consecutive_failures_increment_and_a_successful_sync_clears_the_issue()
    {
        var tenant = TestTenantContext.New();
        var (db, sync, store, provider) = await ArrangeAsync(tenant);
        await using var _ = db;

        provider.Throw = ExceptionFor(ProviderIssueKind.ServiceUnavailable);
        for (var i = 0; i < 3; i++)
        {
            await FluentActions.Awaiting(() => sync.SyncAsync()).Should().ThrowAsync<ProviderException>();
        }

        (await store.ListAsync()).Single().Issue!.ConsecutiveFailures.Should().Be(3);

        provider.Throw = null; // the provider recovered
        await sync.SyncAsync();

        (await store.ListAsync()).Single().Issue.Should().BeNull("recovery clears the issue");
        await using var verify = _fixture.CreateDbContext(tenant);
        var row = await verify.ProviderConnections.AsNoTracking().SingleAsync();
        row.ConsecutiveFailures.Should().Be(0);
        row.IssueKind.Should().BeNull();
        row.IssueRetryAfter.Should().BeNull();

        // And a later failure starts counting from one again.
        provider.Throw = ExceptionFor(ProviderIssueKind.InstitutionUnavailable);
        await FluentActions.Awaiting(() => sync.SyncAsync()).Should().ThrowAsync<ProviderException>();
        (await store.ListAsync()).Single().Issue!.ConsecutiveFailures.Should().Be(1);
    }

    [DockerFact]
    public async Task Retry_after_round_trips_through_the_database()
    {
        var tenant = TestTenantContext.New();
        var (db, sync, store, provider) = await ArrangeAsync(tenant);
        await using var _ = db;
        var until = DateTimeOffset.UtcNow.AddHours(2);
        provider.Throw = new RateLimitedProviderException("slow down", until);

        await FluentActions.Awaiting(() => sync.SyncAsync()).Should().ThrowAsync<RateLimitedProviderException>();

        (await store.ListAsync()).Single().Issue!.RetryAfter.Should().BeCloseTo(until, TimeSpan.FromMilliseconds(5));
    }

    [DockerFact]
    public async Task A_failure_to_record_health_never_masks_the_original_exception_or_the_success()
    {
        var tenant = TestTenantContext.New();
        var (db, sync, _, provider) = await ArrangeAsync(tenant, new ThrowingStore());
        await using var _ = db;

        var original = ExceptionFor(ProviderIssueKind.ReauthRequired);
        provider.Throw = original;
        (await FluentActions.Awaiting(() => sync.SyncAsync()).Should().ThrowAsync<ProviderException>()).Which.Should().BeSameAs(original);

        provider.Throw = null;
        await FluentActions.Awaiting(() => sync.SyncAsync()).Should().NotThrowAsync("a bookkeeping failure must not fail a good sync");
    }

    [DockerFact]
    public async Task Non_provider_exceptions_propagate_and_are_not_recorded_as_provider_issues()
    {
        var tenant = TestTenantContext.New();
        var (db, sync, store, provider) = await ArrangeAsync(tenant);
        await using var _ = db;
        provider.Throw = new InvalidOperationException("local bug");

        await FluentActions.Awaiting(() => sync.SyncAsync()).Should().ThrowAsync<InvalidOperationException>();

        (await store.ListAsync()).Single().Issue.Should().BeNull();
    }

    [DockerFact]
    public async Task Recording_health_for_an_unknown_connection_is_a_no_op()
    {
        var tenant = TestTenantContext.New();
        await using var db = _fixture.CreateDbContext(tenant);
        var store = new EfProviderConnectionStore(db, tenant);

        await store.RecordHealthAsync("Nobody", ProviderIssueMapping.ToInfo(ProviderIssue.Of(ProviderIssueKind.Internal, "x"), DateTimeOffset.UtcNow));

        (await store.ListAsync()).Should().BeEmpty();
    }

    [DockerFact]
    public async Task Health_is_isolated_per_tenant()
    {
        var a = TestTenantContext.New();
        var b = TestTenantContext.New();
        var (dbA, syncA, storeA, providerA) = await ArrangeAsync(a);
        var (dbB, _, storeB, _) = await ArrangeAsync(b);
        await using var _1 = dbA;
        await using var _2 = dbB;
        providerA.Throw = ExceptionFor(ProviderIssueKind.Disconnected);

        await FluentActions.Awaiting(() => syncA.SyncAsync()).Should().ThrowAsync<ProviderException>();

        (await storeA.ListAsync()).Single().Issue.Should().NotBeNull();
        (await storeB.ListAsync()).Single().Issue.Should().BeNull();
    }

    [DockerFact]
    public async Task Issue_message_is_truncated_to_the_column_size()
    {
        var tenant = TestTenantContext.New();
        var (db, sync, store, provider) = await ArrangeAsync(tenant);
        await using var _ = db;
        provider.Throw = new TransientProviderException(new string('x', 5000));

        await FluentActions.Awaiting(() => sync.SyncAsync()).Should().ThrowAsync<ProviderException>();

        (await store.ListAsync()).Single().Issue!.Message.Length.Should().Be(EfProviderConnectionStore.MaxMessageLength);
    }

    private sealed class ThrowingStore : IProviderConnectionStore
    {
        public Task<string?> GetMemberProviderIdAsync(string providerName, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SaveAsync(string providerName, string memberProviderId, string institution, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordHealthAsync(string providerName, ConnectionIssueInfo? issue, CancellationToken ct = default)
            => throw new InvalidOperationException("health store down");
    }

    /// <summary>A connector with no identity of its own: succeeds with no accounts, or throws whatever it is told to.</summary>
    private sealed class FakeProvider : IFinancialDataProvider
    {
        public Exception? Throw { get; set; }
        public string ProviderName => Vendor;
        public ProviderCapabilities Capabilities => new SimulatedFinancialDataProvider(Microsoft.Extensions.Options.Options.Create(new SimulatedProviderOptions())).Capabilities;

        public Task<ProviderMemberRef> BeginConnectionAsync(TenantId t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConnectionStatus> GetConnectionStatusAsync(string m, CancellationToken ct = default) => Task.FromResult(ConnectionStatus.Healthy);

        public Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(string m, CancellationToken ct = default)
            => Throw is null ? Task.FromResult<IReadOnlyList<ProviderAccount>>([]) : Task.FromException<IReadOnlyList<ProviderAccount>>(Throw);

        public Task<TransactionPage> GetTransactionsAsync(string a, SyncCursor c, int p = 100, CancellationToken ct = default) => throw new NotSupportedException();
        public Task VerifyWebhookAsync(ReadOnlyMemory<byte> b, string s, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ProviderWebhookEvent> ParseWebhookAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
