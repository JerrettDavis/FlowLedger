using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Persistence;

[Collection("Integration")]
public sealed class MigrationLockTests(IntegrationTestFixture fixture)
{
    private sealed class CollectingLogger : ILogger<DatabaseMigrationService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }

    private async Task<string> CreateEmptyDatabaseAsync()
    {
        var name = "mig_" + Guid.NewGuid().ToString("N");
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name, Pooling = false }.ConnectionString;
    }

    private static ServiceProvider ProviderFor(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            var options = new DbContextOptionsBuilder<FlowLedgerDbContext>()
                .UseNpgsql(connectionString, n => n.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name))
                .EnableServiceProviderCaching(false)
                .Options;
            return new FlowLedgerDbContext(options, new NoOpDomainEventDispatcher(), FlowLedger.Infrastructure.Persistence.UnscopedTenantContext.Instance);
        });
        return services.BuildServiceProvider();
    }

    [DockerFact]
    public async Task Two_concurrent_starters_migrate_the_database_exactly_once()
    {
        var cs = await CreateEmptyDatabaseAsync();
        using var provider = ProviderFor(cs);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:PreMigrationSnapshot"] = "false",
        }).Build();

        var logA = new CollectingLogger();
        var logB = new CollectingLogger();
        var a = new DatabaseMigrationService(provider, logA, config);
        var b = new DatabaseMigrationService(provider, logB, config);

        await Task.WhenAll(a.StartAsync(CancellationToken.None), b.StartAsync(CancellationToken.None));

        var appliers = new[] { logA, logB }.Count(l => l.Messages.Any(m => m.Contains("pending migration(s) to apply")));
        var skippers = new[] { logA, logB }.Count(l => l.Messages.Any(m => m.Contains("up to date")));
        appliers.Should().Be(1, "only the starter that wins the lock has anything to apply");
        skippers.Should().Be(1, "the loser finds the schema already current");

        await using var ctx = provider.CreateScope().ServiceProvider.GetRequiredService<FlowLedgerDbContext>();
        (await ctx.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [DockerFact]
    public async Task Migration_lock_times_out_with_a_clear_error_when_another_session_holds_it()
    {
        var cs = await CreateEmptyDatabaseAsync();
        await using var held = await PostgresAdvisoryLock.TryAcquireAsync(cs, DatabaseMigrationService.LockName);
        held.Should().NotBeNull();

        var act = () => PostgresAdvisoryLock.AcquireAsync(cs, DatabaseMigrationService.LockName, TimeSpan.FromMilliseconds(600));

        await act.Should().ThrowAsync<TimeoutException>().WithMessage("*migration*");
    }
}
