using System.Globalization;
using System.Text.Json;
using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Integrations.Tests.Contract;

/// <summary>
/// Snapshot tests on mapper output: the fixture goes in through the real provider, the mapped accounts and transactions
/// are projected to stable JSON and compared with <c>Fixtures/snapshots/*.json</c>. A provider schema change that alters
/// what we store (sign, type inference, date, pending flag, currency) fails here with a readable diff.
/// Regenerate deliberately with <c>UPDATE_SNAPSHOTS=1</c> and review the diff.
/// </summary>
public sealed class ProviderMapperSnapshotTests
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private static async Task<string> MapAsync(IFinancialDataProvider provider)
    {
        var member = await provider.BeginConnectionAsync(FlowLedger.Domain.ValueObjects.TenantId.From(Guid.Parse("00000000-0000-0000-0000-000000000001")));
        var accounts = await provider.GetAccountsAsync(member.ProviderId);
        var result = new List<object>();
        foreach (var a in accounts)
        {
            var page = await provider.GetTransactionsAsync(a.ProviderId, SyncCursor.Initial);
            result.Add(new
            {
                a.ProviderId,
                a.Name,
                a.AccountType,
                Balance = a.Balance.Amount,
                Available = a.AvailableBalance?.Amount,
                a.CurrencyCode,
                Transactions = page.Items.Select(t => new
                {
                    t.ProviderId,
                    t.ProviderAccountId,
                    PostedDate = t.PostedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    t.IsPending,
                    Amount = t.Amount.Amount,
                    Currency = t.Amount.Currency.Code,
                    t.RawDescription,
                }),
            });
        }

        return JsonSerializer.Serialize(result, Pretty).ReplaceLineEndings("\n");
    }

    private static void AssertMatchesSnapshot(string actual, string snapshot)
    {
        if (Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1")
        {
            var path = FixtureFiles.SourcePath(snapshot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual + "\n");
        }

        FixtureFiles.Read(snapshot).ReplaceLineEndings("\n").TrimEnd().Should().Be(actual.TrimEnd(),
            "mapper output changed; if intended, regenerate with UPDATE_SNAPSHOTS=1 and review the diff");
    }

    [Fact]
    public async Task SimpleFin_mapper_output_matches_snapshot() =>
        AssertMatchesSnapshot(await MapAsync(FixtureProviders.SimpleFin()), "snapshots/simplefin.json");

    [Fact]
    public async Task Quiltt_mapper_output_matches_snapshot() =>
        AssertMatchesSnapshot(await MapAsync(FixtureProviders.Quiltt()), "snapshots/quiltt.json");
}
