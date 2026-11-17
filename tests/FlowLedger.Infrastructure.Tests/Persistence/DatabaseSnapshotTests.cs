using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Persistence;

[Collection("Integration")]
public sealed class DatabaseSnapshotTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fl-snap-" + Guid.NewGuid().ToString("N"));

    public DatabaseSnapshotTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        return Task.CompletedTask;
    }

    private async Task SeedAsync()
    {
        var tenantCtx = TestTenantContext.New();
        var tenantId = TenantId.From(tenantCtx.TenantId);
        await using var ctx = _fixture.CreateDbContext(tenantCtx);
        // Names with commas, quotes and newlines exercise CSV quoting in the record counter.
        ctx.Accounts.Add(Account.Create(tenantId, "Checking, \"main\"\nline2", AccountType.Checking,
            new Money(10m, Currency.Usd), institution: "Bank"));
        ctx.Accounts.Add(Account.Create(tenantId, "Savings", AccountType.Savings, new Money(20m, Currency.Usd)));
        await ctx.SaveChangesAsync();
    }

    [DockerFact]
    public async Task Snapshot_IsVerifiedAndMatchesLiveCounts()
    {
        await SeedAsync();
        var svc = new DatabaseSnapshotService(_fixture.ConnectionString);

        var (dir, manifest) = await svc.CreateAsync(_root, "test");

        manifest.Tables.Single(t => t.Table == "accounts").Rows.Should().Be(2);
        manifest.AppliedMigrations.Should().NotBeEmpty();
        (await DatabaseSnapshotService.VerifyAsync(dir)).Ok.Should().BeTrue();

        var live = await svc.CountLiveRowsAsync(manifest.Tables.Select(t => t.Table));
        foreach (var t in manifest.Tables)
        {
            live[t.Table].Should().Be(t.Rows, t.Table);
        }
    }

    [DockerFact]
    public async Task Verify_DetectsTampering()
    {
        await SeedAsync();
        var (dir, _) = await new DatabaseSnapshotService(_fixture.ConnectionString).CreateAsync(_root, "test");

        await File.AppendAllTextAsync(Path.Combine(dir, "accounts.csv"), "tampered\n");

        var result = await DatabaseSnapshotService.VerifyAsync(dir);
        result.Ok.Should().BeFalse();
        result.Problems.Should().Contain(p => p.Contains("accounts.csv"));
    }
}
