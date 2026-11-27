namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

    // Bill sheet ---------------------------------------------------------------

    public Task<SheetViewDto?> GetSheetAsync(string? month = null, int months = 6, int ahead = 0, CancellationToken ct = default)
        => GetJsonAsync<SheetViewDto>(
            "/api/sheet?months=" + months + "&ahead=" + ahead + (string.IsNullOrWhiteSpace(month) ? "" : "&month=" + Uri.EscapeDataString(month)),
            "load the sheet", ct: ct);

    public Task<SheetRefreshResult?> RefreshSheetAsync(CancellationToken ct = default)
        => ExecuteAsync("refresh the sheet", async () =>
        {
            var response = await _http.PostAsync("/api/sheet/refresh", null, ct);
            await ThrowIfFailedAsync(response, "refresh the sheet", ct);
            return await response.Content.ReadFromJsonAsync<SheetRefreshResult>(JsonOptions, ct);
        });

    public Task SaveSheetBillAsync(Guid? id, SaveBillRequest request, CancellationToken ct = default)
        => SendNoContentAsync("save the row", () => id is null
            ? _http.PostAsJsonAsync("/api/sheet/bills", request, JsonOptions, ct)
            : _http.PutAsJsonAsync($"/api/sheet/bills/{id}", request, JsonOptions, ct), ct);

    public Task DeleteSheetBillAsync(Guid id, CancellationToken ct = default)
        => SendNoContentAsync("delete the row", () => _http.DeleteAsync($"/api/sheet/bills/{id}", ct), ct);

    public Task SetSheetPaymentAsync(Guid billId, string month, bool paid, decimal? amount = null, int slot = 0, CancellationToken ct = default)
        => SendNoContentAsync("update the cell", () => _http.PutAsJsonAsync(
            $"/api/sheet/bills/{billId}/payments/{month}", new SetPaymentRequest(paid, amount, slot), JsonOptions, ct), ct);

    /// <param name="slot">One payment of a multi-payment month, or null to reset the whole month.</param>
    public Task ClearSheetPaymentAsync(Guid billId, string month, int? slot = null, CancellationToken ct = default)
        => SendNoContentAsync("reset the cell", () => _http.DeleteAsync(
            $"/api/sheet/bills/{billId}/payments/{month}" + (slot is { } s ? $"?slot={s}" : ""), ct), ct);

    public Task<SheetImportResult?> ImportSheetAsync(string csv, bool replace, CancellationToken ct = default)
        => ExecuteAsync("import the sheet", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/sheet/import", new ImportSheetRequest(csv, replace), JsonOptions, ct);
            await ThrowIfFailedAsync(response, "import the sheet", ct);
            return await response.Content.ReadFromJsonAsync<SheetImportResult>(JsonOptions, ct);
        });

    public Task<List<ImportFormatDto>?> GetImportFormatsAsync(CancellationToken ct = default)
        => ExecuteAsync("load import formats", async () =>
        {
            var response = await _http.GetAsync("/api/imports/formats", ct);
            await ThrowIfFailedAsync(response, "load import formats", ct);
            return await response.Content.ReadFromJsonAsync<List<ImportFormatDto>>(JsonOptions, ct);
        });

    public Task<AutoImportResultDto?> ImportTransactionsAutoAsync(Guid accountId, string content, string? formatId, CancellationToken ct = default)
        => ExecuteAsync("import transactions", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/imports/auto", new { accountId, content, formatId }, JsonOptions, ct);
            await ThrowIfFailedAsync(response, "import transactions", ct);
            return await response.Content.ReadFromJsonAsync<AutoImportResultDto>(JsonOptions, ct);
        });

    private Task<bool> SendNoContentAsync(string description, Func<Task<HttpResponseMessage>> send, CancellationToken ct)
        => ExecuteAsync(description, async () =>
        {
            var response = await send();
            await ThrowIfFailedAsync(response, description, ct);
            return true;
        });
}
