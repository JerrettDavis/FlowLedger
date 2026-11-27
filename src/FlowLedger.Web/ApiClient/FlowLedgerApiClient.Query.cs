using System.Net;
using System.Text.Json;

namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

    // Query & reports ----------------------------------------------------------

    public Task<QuerySchemaDto?> GetQuerySchemaAsync(CancellationToken ct = default)
        => GetJsonAsync<QuerySchemaDto>("/api/query/schema", "load query schema", ct: ct);

    public Task<AskQueryAnswer> AskQueryAsync(string question, CancellationToken ct = default)
        => ExecuteAsync("ask a question", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/query/ask", new { question }, JsonOptions, ct);
            await ThrowIfFailedAsync(response, "ask a question", ct);
            return await response.Content.ReadFromJsonAsync<AskQueryAnswer>(JsonOptions, ct)
                ?? new AskQueryAnswer(question, null, "No answer came back.", 0);
        });

    public Task<QueryRunOutcome> RunQueryAsync(string query, CancellationToken ct = default)
        => ExecuteAsync("run query", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/query/run", new { query }, JsonOptions, ct);
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var message = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "The query is invalid.";
                var pos = doc.RootElement.TryGetProperty("position", out var p) && p.TryGetInt32(out var i) ? i : 0;
                return new QueryRunOutcome(null, message, pos);
            }

            await ThrowIfFailedAsync(response, "run query", ct);
            return new QueryRunOutcome(await response.Content.ReadFromJsonAsync<QueryResult>(JsonOptions, ct), null, 0);
        });

    public async Task<List<SavedReportDto>> GetReportsAsync(CancellationToken ct = default)
        => await GetJsonAsync<List<SavedReportDto>>("/api/reports", "load saved reports", ct: ct) ?? [];

    public Task<SavedReportDto?> GetReportAsync(string id, CancellationToken ct = default)
        => GetJsonAsync<SavedReportDto>($"/api/reports/{Uri.EscapeDataString(id)}", "load report", nullOn404: true, ct: ct);

    public Task<RunReportResultDto?> RunReportAsync(string id, DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default)
        => GetJsonAsync<RunReportResultDto>($"/api/reports/{Uri.EscapeDataString(id)}/run{RangeQuery(from, to)}", "run report", nullOn404: true, ct: ct);

    public Task<SavedReportDto?> SaveReportAsync(SaveReportRequest request, Guid? existingId = null, CancellationToken ct = default)
        => ExecuteAsync("save report", async () =>
        {
            var response = existingId is { } id
                ? await _http.PutAsJsonAsync($"/api/reports/{id}", request, JsonOptions, ct)
                : await _http.PostAsJsonAsync("/api/reports", request, JsonOptions, ct);
            await ThrowIfFailedAsync(response, "save report", ct);
            return await response.Content.ReadFromJsonAsync<SavedReportDto>(JsonOptions, ct);
        });

    public Task<bool> DeleteReportAsync(Guid id, CancellationToken ct = default)
        => ExecuteAsync("delete report", async () =>
        {
            var response = await _http.DeleteAsync($"/api/reports/{id}", ct);
            await ThrowIfFailedAsync(response, "delete report", ct);
            return true;
        });

    public Task<DashboardActivityDto?> GetDashboardActivityAsync(CancellationToken ct = default)
        => GetJsonAsync<DashboardActivityDto>("/api/dashboard/activity", "load dashboard activity", ct: ct);

    /// <summary>Dismisses a flagged transaction from Suspicious Activity, optionally recording why.</summary>
    public async Task<bool> DismissSuspiciousAsync(Guid transactionId, string? note, CancellationToken ct = default) =>
        await ExecuteAsync("dismiss flagged transaction", async () =>
        {
            var response = await _http.PostAsJsonAsync($"/api/dashboard/suspicious/{transactionId}/dismiss", new { note }, JsonOptions, ct);
            return response.IsSuccessStatusCode;
        });
}
