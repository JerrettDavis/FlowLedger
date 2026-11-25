using System.Net;
using System.Text;
using FlowLedger.Infrastructure.Persistence.Entities;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.Integrations.Quiltt;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLedger.Infrastructure.Tests.Sync;

/// <summary>
/// Drives the real DI-built Quiltt provider over real HTTP against a local mock that speaks Quiltt's documented wire shapes
/// (Auth session API + GraphQL), through FinancialSyncService into Postgres. Proves connect → read → populate → idempotent re-sync.
/// </summary>
[Collection("Integration")]
public sealed class QuilttSyncEndToEndTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;

    public QuilttSyncEndToEndTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string Accounts = """
        {"data":{"accounts":[
          {"id":"acct_chk","name":"Everyday","kind":"DEPOSITORY","state":"OPEN","mask":"1234","currencyCode":"USD","balance":{"current":1500.25,"available":1400},"institution":{"name":"Mock Bank"},"connection":{"id":"conn_1","status":"SYNCED"}},
          {"id":"acct_cc","name":"Visa","kind":"CREDIT","state":"OPEN","mask":"9876","currencyCode":"USD","balance":{"current":-320.10},"institution":{"name":"Mock Bank"},"connection":{"id":"conn_1","status":"SYNCED"}}
        ]}}
        """;

    private static string Transactions()
    {
        var d = DateTime.UtcNow.Date;
        string Day(int ago) => d.AddDays(-ago).ToString("yyyy-MM-dd");
        return """
            {"data":{"transactions":{"nodes":[
              {"id":"t1","date":"@D10@","description":"COFFEE SHOP","amount":-4.5,"currencyCode":"USD","status":"POSTED","entryType":"DEBIT","account":{"id":"acct_chk"}},
              {"id":"t2","date":"@D9@","description":"PAYCHECK","amount":2000,"currencyCode":"USD","status":"POSTED","entryType":"CREDIT","account":{"id":"acct_chk"}},
              {"id":"t3","date":"@D8@","description":"GROCERY","amount":-60,"currencyCode":"USD","status":"POSTED","entryType":"DEBIT","account":{"id":"acct_cc"}}
            ],"pageInfo":{"hasNextPage":false}}}}
            """.Replace("@D10@", Day(10)).Replace("@D9@", Day(9)).Replace("@D8@", Day(8));
    }

    [DockerFact]
    public async Task Sync_ThroughRealHttp_PopulatesAccountsAndTransactions_AndIsIdempotent()
    {
        var port = FreePort();
        using var cts = new CancellationTokenSource();
        var requests = new List<string>();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var server = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { break; }
                using var reader = new StreamReader(ctx.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                lock (requests) { requests.Add($"{ctx.Request.Url!.AbsolutePath} auth={ctx.Request.Headers["Authorization"]}"); }

                string json; var status = 200;
                if (ctx.Request.Url!.AbsolutePath.EndsWith("/users/sessions"))
                {
                    status = 201;
                    json = """{"token":"eyJ.e30.sig","userId":"p_mock","environmentId":"env_1","expiration":1893456000,"expiresAt":"2030-01-01T00:00:00Z"}""";
                }
                else if (body.Contains("FlowLedgerAccounts")) { json = Accounts; }
                else { json = Transactions(); }

                var bytes = Encoding.UTF8.GetBytes(json);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Quiltt:Enabled"] = "true",
                ["Quiltt:ApiKeySecret"] = "sk_test",
                ["Quiltt:ConnectorId"] = "con_test",
                ["Quiltt:AuthBaseUrl"] = $"http://127.0.0.1:{port}/auth/v1/",
                ["Quiltt:ApiBaseUrl"] = $"http://127.0.0.1:{port}/api/v1/",
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddQuilttProvider(config);
            await using var sp = services.BuildServiceProvider();

            var tenant = TestTenantContext.New();
            await using (var seed = _fixture.CreateDbContext(tenant))
            {
                // What POST /api/integrations/quiltt/session persists when the user opens the Connector.
                await seed.ProviderConnections.AddAsync(ProviderConnectionRecord.Create(
                    tenant.TenantId, "Quiltt", "p_mock", "Quiltt", "ConnectionPending", DateTimeOffset.UtcNow));
                await seed.SaveChangesAsync();
            }

            async Task<FlowLedger.Application.Abstractions.SyncResult> RunSync()
            {
                await using var scope = sp.CreateAsyncScope();
                await using var db = _fixture.CreateDbContext(tenant);
                var provider = scope.ServiceProvider.GetRequiredService<QuilttFinancialDataProvider>();
                var svc = new FinancialSyncService(provider, db, tenant, new EfSyncCursorStore(db, tenant), NullLogger<FinancialSyncService>.Instance);
                return await svc.SyncAsync();
            }

            var first = await RunSync();
            first.AccountsUpserted.Should().Be(2);
            first.TransactionsAdded.Should().Be(3);

            await using (var verify = _fixture.CreateDbContext(tenant))
            {
                var accounts = await verify.Accounts.ToListAsync();
                accounts.Should().HaveCount(2);
                (await verify.Transactions.CountAsync()).Should().Be(3);
            }

            var second = await RunSync();
            second.TransactionsAdded.Should().Be(0, "re-reading the overlap window must not duplicate");
            await using (var verify = _fixture.CreateDbContext(tenant))
            {
                (await verify.Transactions.CountAsync()).Should().Be(3);
            }

            lock (requests)
            {
                requests.Should().Contain(r => r.StartsWith("/auth/v1/users/sessions") && r.Contains("Bearer sk_test"));
                requests.Should().Contain(r => r.StartsWith("/api/v1/graphql") && r.Contains("Bearer eyJ.e30.sig"));
            }
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            await server;
        }
    }

    /// <summary>Opt-in: syncs the real Quiltt sandbox profile into Postgres. Set QUILTT_LIVE_KEY and QUILTT_LIVE_PROFILE.</summary>
    [DockerFact]
    public async Task Live_Sandbox_Sync_PopulatesDatabase_AndIsIdempotent()
    {
        var key = Environment.GetEnvironmentVariable("QUILTT_LIVE_KEY");
        var profile = Environment.GetEnvironmentVariable("QUILTT_LIVE_PROFILE");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(profile)) { return; }

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Quiltt:Enabled"] = "true",
            ["Quiltt:ApiKeySecret"] = key,
            ["Quiltt:ConnectorId"] = "live",
            ["Quiltt:InitialHistoryDays"] = "5000",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQuilttProvider(config);
        await using var sp = services.BuildServiceProvider();

        var tenant = TestTenantContext.New();
        await using (var seed = _fixture.CreateDbContext(tenant))
        {
            await seed.ProviderConnections.AddAsync(ProviderConnectionRecord.Create(
                tenant.TenantId, "Quiltt", profile, "Quiltt", "ConnectionPending", DateTimeOffset.UtcNow));
            await seed.SaveChangesAsync();
        }

        async Task<FlowLedger.Application.Abstractions.SyncResult> RunSync()
        {
            await using var scope = sp.CreateAsyncScope();
            await using var db = _fixture.CreateDbContext(tenant);
            var provider = scope.ServiceProvider.GetRequiredService<QuilttFinancialDataProvider>();
            return await new FinancialSyncService(provider, db, tenant, new EfSyncCursorStore(db, tenant), NullLogger<FinancialSyncService>.Instance).SyncAsync();
        }

        var first = await RunSync();
        first.AccountsUpserted.Should().BeGreaterThan(0);
        first.TransactionsAdded.Should().BeGreaterThan(0);

        int accounts, txns;
        await using (var v = _fixture.CreateDbContext(tenant))
        {
            accounts = await v.Accounts.CountAsync();
            txns = await v.Transactions.CountAsync();
        }

        accounts.Should().Be(first.AccountsUpserted);
        txns.Should().Be(first.TransactionsAdded);

        var second = await RunSync();
        second.TransactionsAdded.Should().Be(0);
        await using var v2 = _fixture.CreateDbContext(tenant);
        (await v2.Transactions.CountAsync()).Should().Be(txns);
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
