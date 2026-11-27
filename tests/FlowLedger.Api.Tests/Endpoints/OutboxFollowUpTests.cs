using System.Net.Http.Json;
using FlowLedger.Api.Endpoints;
using FlowLedger.Infrastructure.Events;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>
/// Post-import and post-sync side effects run as outbox consumers (#92, #175, #180): the request only records the event; the
/// dispatcher delivers it, and delivery is idempotent.
/// </summary>
[Collection("ApiIntegration")]
public sealed class OutboxFollowUpTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<List<(string Type, bool Processed, bool Dead, int Attempts)>> RowsAsync()
    {
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT event_type, processed_utc IS NOT NULL, dead_lettered_utc IS NOT NULL, attempts FROM outbox_messages ORDER BY occurred_utc", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<(string, bool, bool, int)>();
        while (await r.ReadAsync())
        {
            rows.Add((r.GetString(0), r.GetBoolean(1), r.GetBoolean(2), r.GetInt32(3)));
        }

        return rows;
    }

    /// <summary>Delivers everything due for the demo tenant, as the Worker's dispatcher would.</summary>
    private async Task DrainAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessBatchAsync() > 0)
        {
        }
    }

    [Fact]
    public async Task An_import_records_TransactionsImported_and_delivery_completes_without_dead_letters()
    {
        var account = await (await _client.PostAsJsonAsync("/api/accounts",
            new CreateAccountRequest("Outbox Import", "Checking", 0m, "USD", null, null))).Content.ReadFromJsonAsync<AccountDto>();

        var response = await _client.PostAsJsonAsync("/api/imports", new ImportRequest(
            account!.Id, "Date,Amount,Description\n2026-01-15,50.00,Coffee Shop\n2026-01-16,100.00,Grocery Store\n"));
        response.EnsureSuccessStatusCode();

        (await RowsAsync()).Should().ContainSingle(r => r.Type == "TransactionsImported" && !r.Processed,
            "the request commits the event with the transactions and does not run the side effects inline");

        await DrainAsync();

        (await RowsAsync()).Should().ContainSingle(r => r.Type == "TransactionsImported" && r.Processed && !r.Dead);
    }

    [Fact]
    public async Task An_import_that_adds_nothing_records_no_event()
    {
        var account = await (await _client.PostAsJsonAsync("/api/accounts",
            new CreateAccountRequest("Outbox Empty", "Checking", 0m, "USD", null, null))).Content.ReadFromJsonAsync<AccountDto>();
        var csv = "Date,Amount,Description\n2026-01-15,50.00,Coffee Shop\n";
        (await _client.PostAsJsonAsync("/api/imports", new ImportRequest(account!.Id, csv))).EnsureSuccessStatusCode();
        await DrainAsync();

        (await _client.PostAsJsonAsync("/api/imports", new ImportRequest(account.Id, csv))).EnsureSuccessStatusCode();

        (await RowsAsync()).Count(r => r.Type == "TransactionsImported").Should().Be(1, "the second import was all duplicates");
    }

    [Fact]
    public async Task A_sync_records_SyncCompleted_and_redelivery_is_harmless()
    {
        await _client.PostAsync("/api/connect", null);
        (await _client.PostAsync("/api/sync", null)).EnsureSuccessStatusCode();

        (await RowsAsync()).Should().ContainSingle(r => r.Type == "SyncCompleted" && !r.Processed);

        await DrainAsync();
        var accountsAfterFirstDelivery = await _client.GetFromJsonAsync<List<AccountDto>>("/api/accounts");

        // Simulate an at-least-once replay: make the delivered message due again with its handler progress forgotten.
        await using (var conn = new NpgsqlConnection(factory.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "UPDATE outbox_messages SET processed_utc = NULL, completed_handlers = '', next_attempt_utc = occurred_utc", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        await DrainAsync();

        (await RowsAsync()).Should().ContainSingle(r => r.Type == "SyncCompleted" && r.Processed && !r.Dead);
        (await _client.GetFromJsonAsync<List<AccountDto>>("/api/accounts"))!.Select(a => a.Id)
            .Should().BeEquivalentTo(accountsAfterFirstDelivery!.Select(a => a.Id));
    }
}
