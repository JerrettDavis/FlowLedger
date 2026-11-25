using System.Text.Json;
using FlowLedger.Application;
using FlowLedger.Application.Features.LocalAi;
using FlowLedger.Infrastructure;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.SharedKernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Infrastructure.Tests.Ai;

/// <summary>
/// Opt-in harness: restores a verified snapshot of a real household into a scratch database and runs the app's own
/// held-out-merchant evaluator (<see cref="LocalAiService.EvaluateAsync"/>) with each embedder. Does nothing unless
/// FLOWLEDGER_REAL_SNAPSHOT (snapshot dir) and FLOWLEDGER_REAL_PG (admin connection string to a throwaway server) are set.
/// Writes AGGREGATE metrics only to FLOWLEDGER_REAL_OUT; no merchant names are emitted.
/// </summary>
public class RealDataEvaluationTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private sealed class FixedTenant : ITenantContext
    {
        public Guid TenantId => RealDataEvaluationTests.TenantId;
        public Guid UserId => Guid.Empty;
    }

    [Fact]
    public async Task Evaluate_real_snapshot_with_every_embedder()
    {
        var snapshot = Environment.GetEnvironmentVariable("FLOWLEDGER_REAL_SNAPSHOT");
        var pg = Environment.GetEnvironmentVariable("FLOWLEDGER_REAL_PG");
        var outPath = Environment.GetEnvironmentVariable("FLOWLEDGER_REAL_OUT");
        if (string.IsNullOrEmpty(snapshot) || string.IsNullOrEmpty(pg) || string.IsNullOrEmpty(outPath))
        {
            return; // opt-in only; vacuous in CI
        }

        var restore = await SnapshotRestoreDrill.RunAsync(pg, snapshot, "flowledger_real", keep: true);
        Assert.True(restore.Ok, string.Join("; ", restore.Problems));

        var scratch = new Npgsql.NpgsqlConnectionStringBuilder(pg) { Database = "flowledger_real" }.ConnectionString;
        var results = new List<object>();
        foreach (var (embedder, model) in new[] { ("Hashing", "minilm-l6-v2-q8"), ("Onnx", "minilm-l6-v2-q8"), ("Onnx", "bge-small-en-v1.5-q8") })
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:flowledger"] = scratch,
                ["LocalAi:Embedder"] = embedder,
                ["LocalAi:EmbeddingModel"] = model,
            }).Build();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(config);
            services.AddLogging();
            services.AddApplication();
            services.AddInfrastructure(config);
            services.AddScoped<ITenantContext>(_ => new FixedTenant());
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<LocalAiService>();
            foreach (var folds in new[] { 5, 10 })
            {
                var r = await svc.EvaluateAsync(folds);
                results.Add(new { Configured = $"{embedder}/{model}", folds, r });
            }
        }

        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}
