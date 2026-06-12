using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Tests.Fakes;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features.Categories;

public sealed class CategoryHandlersTests
{
    private readonly FakeCategoryRepository _repo = new();
    private readonly FakeTenantContext _tenant = new();

    [Fact]
    public async Task CreateCategory_ValidRequest_StoresAndReturnsDto()
    {
        var handler = new CreateCategoryHandler(_repo, _tenant);
        var dto = await handler.HandleAsync(new CreateCategoryRequest("Food/Groceries", "Groceries", null));

        dto.Path.Should().Be("Food/Groceries");
        dto.DisplayName.Should().Be("Groceries");
        dto.IsSystem.Should().BeFalse();
    }

    [Fact]
    public async Task GetCategory_ExistingId_ReturnsDto()
    {
        var createHandler = new CreateCategoryHandler(_repo, _tenant);
        var created = await createHandler.HandleAsync(new CreateCategoryRequest("Housing", "Housing", null));

        var getHandler = new GetCategoryHandler(_repo);
        var dto = await getHandler.HandleAsync(created.Id);

        dto.Should().NotBeNull();
        dto!.Path.Should().Be("Housing");
    }

    [Fact]
    public async Task ListCategories_ReturnsAll()
    {
        var createHandler = new CreateCategoryHandler(_repo, _tenant);
        await createHandler.HandleAsync(new CreateCategoryRequest("Food", "Food", null));
        await createHandler.HandleAsync(new CreateCategoryRequest("Transport", "Transport", null));

        var listHandler = new ListCategoriesHandler(_repo);
        var categories = await listHandler.HandleAsync();

        categories.Should().HaveCount(2);
    }

    [Fact]
    public async Task UpdateCategory_ExistingId_RenamesCategory()
    {
        var createHandler = new CreateCategoryHandler(_repo, _tenant);
        var created = await createHandler.HandleAsync(new CreateCategoryRequest("OldPath", "Old Name", null));

        var updateHandler = new UpdateCategoryHandler(_repo);
        var updated = await updateHandler.HandleAsync(created.Id, new UpdateCategoryRequest("NewPath", "New Name"));

        updated.Should().NotBeNull();
        updated!.DisplayName.Should().Be("New Name");
        updated.Path.Should().Be("NewPath");
    }

    [Fact]
    public async Task DeleteCategory_NonSystem_Succeeds()
    {
        var createHandler = new CreateCategoryHandler(_repo, _tenant);
        var created = await createHandler.HandleAsync(new CreateCategoryRequest("Temp", "Temp", null));

        var deleteHandler = new DeleteCategoryHandler(_repo);
        var found = await deleteHandler.HandleAsync(created.Id);

        found.Should().BeTrue();
        _repo.All.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteCategory_UnknownId_ReturnsFalse()
    {
        var handler = new DeleteCategoryHandler(_repo);
        var result = await handler.HandleAsync(Guid.NewGuid());
        result.Should().BeFalse();
    }
}
