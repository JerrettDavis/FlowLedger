using System.Net;
using System.Net.Http.Json;
using FlowLedger.Api.Endpoints;
using FlowLedger.Application.Features.Accounts;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

[Collection("ApiIntegration")]
public sealed class ImportEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    private const string TestCsv =
        "Date,Amount,Description\n" +
        "2026-01-15,50.00,Coffee Shop\n" +
        "2026-01-16,100.00,Grocery Store\n";

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Import ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_imports_csv_and_returns_200_with_imported_count()
    {
        var account = await CreateAccount();
        var request = new ImportRequest(
            AccountId: account.Id,
            CsvContent: TestCsv,
            DateColumnIndex: 0,
            AmountColumnIndex: 1,
            DescriptionColumnIndex: 2,
            HasHeaderRow: true);

        var response = await _client.PostAsJsonAsync("/api/imports", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = await response.Content.ReadFromJsonAsync<ImportSummaryDto>();
        summary.Should().NotBeNull();
        summary!.ImportedCount.Should().Be(2);
        summary.DuplicateCount.Should().Be(0);
        summary.FailedRowCount.Should().Be(0);
    }

    [Fact]
    public async Task Post_second_import_of_same_csv_deduplicates()
    {
        var account = await CreateAccount();
        var request = new ImportRequest(
            AccountId: account.Id,
            CsvContent: TestCsv,
            DateColumnIndex: 0,
            AmountColumnIndex: 1,
            DescriptionColumnIndex: 2,
            HasHeaderRow: true);

        // First import
        var first = await _client.PostAsJsonAsync("/api/imports", request);
        first.EnsureSuccessStatusCode();

        // Second import — same content
        var second = await _client.PostAsJsonAsync("/api/imports", request);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var summary = await second.Content.ReadFromJsonAsync<ImportSummaryDto>();
        summary.Should().NotBeNull();
        summary!.ImportedCount.Should().Be(0);
        summary.DuplicateCount.Should().Be(2);
    }

    [Fact]
    public async Task Post_with_empty_csv_content_returns_400()
    {
        var account = await CreateAccount();
        var request = new ImportRequest(
            AccountId: account.Id,
            CsvContent: string.Empty);

        var response = await _client.PostAsJsonAsync("/api/imports", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_with_empty_account_id_returns_400()
    {
        var request = new ImportRequest(
            AccountId: Guid.Empty,
            CsvContent: TestCsv,
            HasHeaderRow: true);

        var response = await _client.PostAsJsonAsync("/api/imports", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Unauthenticated ───────────────────────────────────────────────────────

    [Fact]
    public async Task Post_without_auth_returns_401()
    {
        using var anon = factory.CreateClient();
        var request = new ImportRequest(
            AccountId: Guid.NewGuid(),
            CsvContent: TestCsv);

        var response = await anon.PostAsJsonAsync("/api/imports", request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<AccountDto> CreateAccount()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/accounts",
            new CreateAccountRequest("Import Test Account", "Checking", 0m, "USD", null, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    private sealed record ImportSummaryDto(
        Guid ImportBatchId,
        int ImportedCount,
        int DuplicateCount,
        int FailedRowCount);
}
