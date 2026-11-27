namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

    // ── Integrations ──────────────────────────────────────────────────────────

    public async Task<IntegrationsStateDto?> GetIntegrationsAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("read integrations", async () =>
        {
            var response = await _http.GetAsync("/api/integrations", ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<IntegrationsStateDto>(JsonOptions, ct) : null;
        });
    }

    public async Task<IntegrationSavedDto?> SaveIntegrationAsync(Guid? id, IntegrationSaveDto request, CancellationToken ct = default)
    {
        return await ExecuteAsync("save the integration", async () =>
        {
            var response = id is { } existing
                ? await _http.PutAsJsonAsync($"/api/integrations/{existing}", request, JsonOptions, ct)
                : await _http.PostAsJsonAsync("/api/integrations", request, JsonOptions, ct);
            await ThrowIntegrationErrorAsync(response, "Couldn't save the integration", ct);
            return await response.Content.ReadFromJsonAsync<IntegrationSavedDto>(JsonOptions, ct);
        });
    }

    public async Task DeleteIntegrationAsync(Guid id, CancellationToken ct = default)
    {
        await ExecuteAsync("delete the integration", async () =>
        {
            var response = await _http.DeleteAsync($"/api/integrations/{id}", ct);
            await ThrowIntegrationErrorAsync(response, "Couldn't delete the integration", ct);
            return true;
        });
    }

    public async Task<IntegrationSavedDto?> RotateIntegrationSecretAsync(Guid id, CancellationToken ct = default)
    {
        return await ExecuteAsync("rotate the signing secret", async () =>
        {
            var response = await _http.PostAsync($"/api/integrations/{id}/rotate-secret", null, ct);
            await ThrowIntegrationErrorAsync(response, "Couldn't rotate the signing secret", ct);
            return await response.Content.ReadFromJsonAsync<IntegrationSavedDto>(JsonOptions, ct);
        });
    }

    public async Task<List<IntegrationDeliveryDto>?> GetIntegrationDeliveriesAsync(Guid id, CancellationToken ct = default)
    {
        return await ExecuteAsync("read the delivery log", async () =>
        {
            var response = await _http.GetAsync($"/api/integrations/{id}/deliveries", ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<List<IntegrationDeliveryDto>>(JsonOptions, ct) : null;
        });
    }

    public Task<IntegrationCallResultDto?> TestIntegrationAsync(Guid id, CancellationToken ct = default)
        => PostIntegrationCallAsync($"/api/integrations/{id}/test", (object?)null, "send the test event", ct);

    public Task<IntegrationCallResultDto?> CallRestIntegrationAsync(Guid id, RestCallDto call, CancellationToken ct = default)
        => PostIntegrationCallAsync($"/api/integrations/{id}/rest/call", call, "send the request", ct);

    public Task<IntegrationCallResultDto?> InvokeSoapAsync(Guid id, SoapInvokeDto call, CancellationToken ct = default)
        => PostIntegrationCallAsync($"/api/integrations/{id}/soap/invoke", call, "call the operation", ct);

    public async Task<SoapOperationsDto?> GetSoapOperationsAsync(Guid id, CancellationToken ct = default)
    {
        return await ExecuteAsync("read the WSDL", async () =>
        {
            var response = await _http.PostAsync($"/api/integrations/{id}/soap/operations", null, ct);
            await ThrowIntegrationErrorAsync(response, "Couldn't read the WSDL", ct);
            return await response.Content.ReadFromJsonAsync<SoapOperationsDto>(JsonOptions, ct);
        });
    }

    private async Task<IntegrationCallResultDto?> PostIntegrationCallAsync(string url, object? body, string what, CancellationToken ct)
    {
        return await ExecuteAsync(what, async () =>
        {
            var response = body is null
                ? await _http.PostAsync(url, null, ct)
                : await _http.PostAsync(url, System.Net.Http.Json.JsonContent.Create(body, body.GetType(), options: JsonOptions), ct);
            await ThrowIntegrationErrorAsync(response, $"Couldn't {what}", ct);
            return await response.Content.ReadFromJsonAsync<IntegrationCallResultDto>(JsonOptions, ct);
        });
    }

    private static async Task ThrowIntegrationErrorAsync(HttpResponseMessage response, string fallback, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var problem = await ReadProblemAsync(response, ct);
            throw new ApiClientException(
                problem.ValidationMessage ?? await ReadProblemDetailAsync(response, ct)
                    ?? $"{fallback} (server returned {(int)response.StatusCode}).",
                response.StatusCode,
                fieldErrors: problem.FieldErrors);
        }
    }

    public async Task<List<GoalDto>?> GetGoalsAsync(CancellationToken ct = default)
    {
        return await ExecuteAsync("read goals", async () =>
        {
            var response = await _http.GetAsync("/api/goals", ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<List<GoalDto>>(JsonOptions, ct) : null;
        });
    }

    public async Task SaveGoalAsync(Guid? id, string name, decimal targetAmount, decimal savedAmount, DateOnly? targetDate, uint? expectedVersion = null, Guid? linkedAccountId = null, CancellationToken ct = default)
    {
        await ExecuteAsync("save goal", async () =>
        {
            var response = await SendVersionedAsync(HttpMethod.Put, "/api/goals", new { id, name, targetAmount, savedAmount, targetDate, linkedAccountId }, expectedVersion, ct);
            ThrowIfChangedElsewhere(response, "goal");
            await ThrowIfFailedAsync(response, "save goal", ct);

            return true;
        });
    }

    public async Task DeleteGoalAsync(Guid id, uint? expectedVersion = null, CancellationToken ct = default)
    {
        await ExecuteAsync("remove goal", async () =>
        {
            var response = await SendVersionedAsync(HttpMethod.Delete, $"/api/goals/{id}", null, expectedVersion, ct);
            ThrowIfChangedElsewhere(response, "goal");
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't remove goal (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return true;
        });
    }

    /// <summary>Begins (or resumes) the real bank connection and returns the provider widget URL. Billable on first use.</summary>
    /// <summary>Provider-specific connect info: SimpleFIN has no widget, so this returns the Bridge page to link institutions at.</summary>
    public async Task<ProviderInfoDto?> GetProviderInfoAsync(CancellationToken ct = default) =>
        await ExecuteAsync("read provider info", async () =>
        {
            var response = await _http.GetAsync("/api/integrations/provider", ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<ProviderInfoDto>(JsonOptions, ct)
                : null;
        });

    /// <summary>Connector overview (configured / active / usage / linked institutions). Null when the page is disabled or not allowed for this user.</summary>
    public async Task<ConnectorsOverviewDto?> GetConnectorsAsync(CancellationToken ct = default) =>
        await ExecuteAsync("read connectors", async () =>
        {
            var response = await _http.GetAsync("/api/integrations/connectors", ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<ConnectorsOverviewDto>(JsonOptions, ct)
                : null;
        });
}
