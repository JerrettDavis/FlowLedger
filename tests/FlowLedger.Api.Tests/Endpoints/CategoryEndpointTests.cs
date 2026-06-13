using System.Net;
using System.Net.Http.Json;
using FlowLedger.Application.Features.Categories;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.SharedKernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Api.Tests.Endpoints;

[Collection("ApiIntegration")]
public sealed class CategoryEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CreateCategoryRequest ValidRequest(string path = "food", string display = "Food") =>
        new(path, display, null);

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_creates_category_and_returns_201()
    {
        var response = await _client.PostAsJsonAsync("/api/categories", ValidRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await response.Content.ReadFromJsonAsync<CategoryDto>();
        dto.Should().NotBeNull();
        dto!.Path.Should().Be("food");
        dto.DisplayName.Should().Be("Food");
        dto.IsSystem.Should().BeFalse();
        response.Headers.Location.Should().NotBeNull();
    }

    // ── Get by id ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_by_id_returns_200_for_existing_category()
    {
        var created = await CreateCategory();

        var response = await _client.GetAsync($"/api/categories/{created.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<CategoryDto>();
        dto!.Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task Get_by_id_returns_404_for_missing_category()
    {
        var response = await _client.GetAsync($"/api/categories/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── List ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_list_returns_200_and_includes_created_category()
    {
        var created = await CreateCategory("transport", "Transport");

        var response = await _client.GetAsync("/api/categories");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await response.Content.ReadFromJsonAsync<List<CategoryDto>>();
        list.Should().NotBeNull();
        list!.Should().Contain(c => c.Id == created.Id);
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_updates_category_and_returns_200()
    {
        var created = await CreateCategory();

        var response = await _client.PutAsJsonAsync(
            $"/api/categories/{created.Id}",
            new UpdateCategoryRequest("food-v2", "Food (Updated)"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<CategoryDto>();
        dto!.DisplayName.Should().Be("Food (Updated)");
        dto.Path.Should().Be("food-v2");
    }

    [Fact]
    public async Task Put_returns_404_for_missing_category()
    {
        var response = await _client.PutAsJsonAsync(
            $"/api/categories/{Guid.NewGuid()}",
            new UpdateCategoryRequest("x", "X"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_returns_204_for_existing_category()
    {
        var created = await CreateCategory();

        var response = await _client.DeleteAsync($"/api/categories/{created.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_returns_404_for_missing_category()
    {
        var response = await _client.DeleteAsync($"/api/categories/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_system_category_returns_409()
    {
        // System categories cannot be created via the API (Category.Create always sets IsSystem=false).
        // Insert one directly via EF to test the handler's guard.
        var systemCategoryId = await InsertSystemCategoryAsync();

        var response = await _client.DeleteAsync($"/api/categories/{systemCategoryId}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_with_empty_path_returns_400()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/categories",
            new CreateCategoryRequest(string.Empty, "Food", null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_with_empty_display_name_returns_400()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/categories",
            new CreateCategoryRequest("food", string.Empty, null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Tenant isolation ──────────────────────────────────────────────────────

    [Fact]
    public async Task Category_is_not_visible_to_different_tenant()
    {
        var created = await CreateCategory("my-category", "My Category");

        using var otherClient = factory.CreateAuthenticatedClient(Guid.NewGuid());
        var listResponse = await otherClient.GetAsync("/api/categories");
        var list = await listResponse.Content.ReadFromJsonAsync<List<CategoryDto>>();

        list.Should().NotContain(c => c.Id == created.Id);

        var getResponse = await otherClient.GetAsync($"/api/categories/{created.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<CategoryDto> CreateCategory(string path = "food", string display = "Food")
    {
        var response = await _client.PostAsJsonAsync("/api/categories", new CreateCategoryRequest(path, display, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CategoryDto>())!;
    }

    private async Task<Guid> InsertSystemCategoryAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowLedgerDbContext>();

        var categoryId = CategoryId.New();
        var category = new Category(
            categoryId,
            TenantId.From(FlowLedgerApiFactory.DemoTenantId),
            new CategoryPath("system-test"),
            "System Test Category",
            isSystem: true);

        db.Set<Category>().Add(category);
        await db.SaveChangesAsync();
        return categoryId.Value;
    }
}
