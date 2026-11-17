using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Persistence;

[Collection("Integration")]
public sealed class DataEnvironmentGuardTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;

    public DataEnvironmentGuardTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DROP TABLE IF EXISTS system_settings", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedAccountAsync()
    {
        var tenantCtx = TestTenantContext.New();
        await using var ctx = _fixture.CreateDbContext(tenantCtx);
        ctx.Accounts.Add(Account.Create(TenantId.From(tenantCtx.TenantId), "Demo", AccountType.Checking, new Money(1m, Currency.Usd)));
        await ctx.SaveChangesAsync();
    }

    [Theory]
    [InlineData(false, null, DataEnvironment.Demo)]
    [InlineData(true, "sandbox", DataEnvironment.Sandbox)]
    [InlineData(true, "production", DataEnvironment.Production)]
    public void Expected_MapsConfiguration(bool enabled, string? env, DataEnvironment expected)
    {
        DataEnvironmentGuard.Expected(enabled, env).Should().Be(expected);
    }

    [DockerFact]
    public async Task EmptyDatabase_IsStampedWithExpected()
    {
        var env = await DataEnvironmentGuard.EnsureAsync(_fixture.ConnectionString, DataEnvironment.Production, false);
        env.Should().Be(DataEnvironment.Production);
    }

    [DockerFact]
    public async Task DemoData_WithoutMarker_RefusesProduction_ButAllowsDemo()
    {
        await SeedAccountAsync();

        var act = () => DataEnvironmentGuard.EnsureAsync(_fixture.ConnectionString, DataEnvironment.Production, false);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*FRESH database*");

        (await DataEnvironmentGuard.EnsureAsync(_fixture.ConnectionString, DataEnvironment.Demo, false))
            .Should().Be(DataEnvironment.Demo);
    }

    [DockerFact]
    public async Task ProductionMarker_BlocksDemoAndSandbox_AndViceVersa()
    {
        await DataEnvironmentGuard.EnsureAsync(_fixture.ConnectionString, DataEnvironment.Production, false);
        var toDemo = () => DataEnvironmentGuard.EnsureAsync(_fixture.ConnectionString, DataEnvironment.Demo, false);
        await toDemo.Should().ThrowAsync<InvalidOperationException>().WithMessage("*never share a database*");
        var toSandbox = () => DataEnvironmentGuard.EnsureAsync(_fixture.ConnectionString, DataEnvironment.Sandbox, false);
        await toSandbox.Should().ThrowAsync<InvalidOperationException>();

        await _fixture.ResetAsync();
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("DELETE FROM system_settings", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await DataEnvironmentGuard.EnsureAsync(_fixture.ConnectionString, DataEnvironment.Demo, false);
        var toProd = () => DataEnvironmentGuard.EnsureAsync(_fixture.ConnectionString, DataEnvironment.Production, false);
        await toProd.Should().ThrowAsync<InvalidOperationException>();
    }

    [DockerFact]
    public async Task DemoAndSandbox_AreInterchangeable_AndAdoptFlagWorks()
    {
        await DataEnvironmentGuard.EnsureAsync(_fixture.ConnectionString, DataEnvironment.Demo, false);
        (await DataEnvironmentGuard.EnsureAsync(_fixture.ConnectionString, DataEnvironment.Sandbox, false))
            .Should().Be(DataEnvironment.Sandbox);

        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("DELETE FROM system_settings", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SeedAccountAsync();
        (await DataEnvironmentGuard.EnsureAsync(_fixture.ConnectionString, DataEnvironment.Production, adoptExistingAsProduction: true))
            .Should().Be(DataEnvironment.Production);
    }
}
