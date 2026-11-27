using System.Net;
using System.Net.Http.Json;
using FlowLedger.TestSupport.Builders;
using FlowLedger.Api.Tests.Builders;
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
        var request = ImportRequestBuilder.A().IntoAccount(account.Id).OfCsv(TestCsv).Columns(0, 1, 2).WithHeaderRow(true).Build();

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
        var request = ImportRequestBuilder.A().IntoAccount(account.Id).OfCsv(TestCsv).Columns(0, 1, 2).WithHeaderRow(true).Build();

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
        var request = ImportRequestBuilder.A().IntoAccount(account.Id).OfCsv(string.Empty).Build();

        var response = await _client.PostAsJsonAsync("/api/imports", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_with_empty_account_id_returns_400()
    {
        var request = ImportRequestBuilder.A().IntoAccount(Guid.Empty).OfCsv(TestCsv).WithHeaderRow(true).Build();

        var response = await _client.PostAsJsonAsync("/api/imports", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Unauthenticated ───────────────────────────────────────────────────────

    [Fact]
    public async Task Post_without_auth_returns_401()
    {
        using var anon = factory.CreateClient();
        var request = ImportRequestBuilder.A().OfCsv(TestCsv).Build();

        var response = await anon.PostAsJsonAsync("/api/imports", request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<AccountDto> CreateAccount()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/accounts",
            CreateAccountRequestBuilder.A().Named("Import Test Account").OfType("Checking").StartingAt(0m).Build());
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    private sealed record ImportSummaryDto(
        Guid ImportBatchId,
        int ImportedCount,
        int DuplicateCount,
        int FailedRowCount);
}
