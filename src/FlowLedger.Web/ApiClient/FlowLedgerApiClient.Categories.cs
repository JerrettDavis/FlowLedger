using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Web.ApiClient;

public sealed partial class FlowLedgerApiClient
{

    // ── Categories ────────────────────────────────────────────────────────────

    public async Task<List<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var result = await GetJsonAsync<List<CategoryDto>>(
            "/api/categories", "load categories", ct: ct);
        return result ?? [];
    }

    /// <summary>Creates a category at <paramref name="path"/> (e.g. "Food/Takeout") and returns it.</summary>
    public async Task<CategoryDto> CreateCategoryAsync(string path, CancellationToken ct = default)
    {
        return await ExecuteAsync("create category", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/categories",
                new { path, displayName = path.Split('/').Last(), parentId = (Guid?)null }, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiClientException($"Couldn't create category (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return (await response.Content.ReadFromJsonAsync<CategoryDto>(JsonOptions, ct))!;
        });
    }

    /// <summary>Renames/moves a category path (and everything under it) to <paramref name="newPath"/>; existing paths there are merged into. <paramref name="dryRun"/> only previews.</summary>
    public async Task<CategoryMoveResultDto> MoveCategoryAsync(string path, string newPath, bool dryRun, CancellationToken ct = default)
    {
        return await ExecuteAsync("move category", async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/categories/move", new { path, newPath, dryRun }, ct);
            if (!response.IsSuccessStatusCode)
            {
                var detail = response.StatusCode == System.Net.HttpStatusCode.BadRequest ? await ReadValidationMessageAsync(response, ct) : null;
                throw new ApiClientException(detail ?? $"Couldn't move category (server returned {(int)response.StatusCode}).", response.StatusCode);
            }

            return (await response.Content.ReadFromJsonAsync<CategoryMoveResultDto>(JsonOptions, ct))!;
        });
    }

    private static async Task<string?> ReadValidationMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: ct);
            if (body.TryGetProperty("errors", out var errors))
            {
                foreach (var prop in errors.EnumerateObject())
                {
                    foreach (var msg in prop.Value.EnumerateArray())
                    {
                        return msg.GetString();
                    }
                }
            }
        }
        catch (Exception)
        {
            // fall through to the generic message
        }

        return null;
    }
}
