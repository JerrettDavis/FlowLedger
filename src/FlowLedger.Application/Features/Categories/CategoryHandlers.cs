using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.Categories;

/// <summary>Lists all categories for the current tenant.</summary>
public sealed class ListCategoriesHandler
{
    private readonly ICategoryRepository _repo;
    private readonly ITenantContext _tenant;

    public ListCategoriesHandler(ICategoryRepository repo, ITenantContext tenant)
    {
        _repo = repo;
        _tenant = tenant;
    }

    public async Task<IReadOnlyList<CategoryDto>> HandleAsync(CancellationToken ct = default)
    {
        var cats = await _repo.ListAsync(ct);

        // First use: a brand-new tenant gets the default (Mint-equivalent) set. Later restructuring is never undone here;
        // use RestoreDefaultCategoriesHandler to add any that are missing.
        if (cats.Count == 0)
        {
            await new RestoreDefaultCategoriesHandler(_repo, _tenant).HandleAsync(ct);
            cats = await _repo.ListAsync(ct);
        }

        return cats.Select(CategoryMapper.ToDto).ToList().AsReadOnly();
    }
}

/// <summary>Additively creates any default categories the tenant is missing; existing ones are never touched.</summary>
public sealed class RestoreDefaultCategoriesHandler
{
    private readonly ICategoryRepository _repo;
    private readonly ITenantContext _tenant;

    public RestoreDefaultCategoriesHandler(ICategoryRepository repo, ITenantContext tenant)
    {
        _repo = repo;
        _tenant = tenant;
    }

    /// <returns>The number of categories added.</returns>
    public async Task<int> HandleAsync(CancellationToken ct = default)
    {
        var have = (await _repo.ListAsync(ct)).Select(c => c.Path.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = DefaultCategories.Paths.Where(p => !have.Contains(p)).ToList();
        var tenantId = TenantId.From(_tenant.TenantId);
        foreach (var path in missing)
        {
            await _repo.AddAsync(Category.Create(tenantId, new CategoryPath(path), path.Split('/').Last()), ct);
        }

        if (missing.Count > 0)
        {
            await _repo.SaveChangesAsync(ct);
        }

        return missing.Count;
    }
}

/// <summary>Returns a single category by ID.</summary>
public sealed class GetCategoryHandler
{
    private readonly ICategoryRepository _repo;

    public GetCategoryHandler(ICategoryRepository repo)
        => _repo = repo;

    public async Task<CategoryDto?> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var cat = await _repo.GetByIdAsync(CategoryId.From(id), ct);
        return cat is null ? null : CategoryMapper.ToDto(cat);
    }
}

/// <summary>Creates a new category for the current tenant.</summary>
public sealed class CreateCategoryHandler
{
    private readonly ICategoryRepository _repo;
    private readonly ITenantContext _tenant;

    public CreateCategoryHandler(ICategoryRepository repo, ITenantContext tenant)
    {
        _repo = repo;
        _tenant = tenant;
    }

    public async Task<CategoryDto> HandleAsync(CreateCategoryRequest request, CancellationToken ct = default)
    {
        var path = new CategoryPath(request.Path);
        CategoryId? parentId = request.ParentId.HasValue
            ? CategoryId.From(request.ParentId.Value)
            : null;

        var category = Category.Create(
            TenantId.From(_tenant.TenantId),
            path,
            request.DisplayName,
            parentId);

        await _repo.AddAsync(category, ct);
        await _repo.SaveChangesAsync(ct);

        return CategoryMapper.ToDto(category);
    }
}

/// <summary>Renames a category.</summary>
public sealed class UpdateCategoryHandler
{
    private readonly ICategoryRepository _repo;

    public UpdateCategoryHandler(ICategoryRepository repo)
        => _repo = repo;

    public async Task<CategoryDto?> HandleAsync(Guid id, UpdateCategoryRequest request, CancellationToken ct = default)
    {
        var cat = await _repo.GetByIdAsync(CategoryId.From(id), ct);
        if (cat is null)
        {
            return null;
        }

        cat.Rename(new CategoryPath(request.Path), request.DisplayName);
        await _repo.SaveChangesAsync(ct);

        return CategoryMapper.ToDto(cat);
    }
}

/// <summary>Deletes a non-system category.</summary>
public sealed class DeleteCategoryHandler
{
    private readonly ICategoryRepository _repo;

    public DeleteCategoryHandler(ICategoryRepository repo)
        => _repo = repo;

    public async Task<bool> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var cat = await _repo.GetByIdAsync(CategoryId.From(id), ct);
        if (cat is null)
        {
            return false;
        }

        if (cat.IsSystem)
        {
            throw new InvalidOperationException("System categories cannot be deleted.");
        }

        await _repo.RemoveAsync(cat, ct);
        await _repo.SaveChangesAsync(ct);
        return true;
    }
}

internal static class CategoryMapper
{
    public static CategoryDto ToDto(Category c) => new(
        c.Id,
        c.Path.Value,
        c.DisplayName,
        c.IsSystem,
        c.ParentId?.Value);
}
