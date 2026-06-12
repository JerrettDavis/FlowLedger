using FlowLedger.Domain.Exceptions;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.SharedKernel;

namespace FlowLedger.Domain.Aggregates;

/// <summary>
/// Category entity. Categories are tenant-scoped and organised in a hierarchy via
/// <see cref="CategoryPath"/>. A system-level category (IsSystem=true) cannot be deleted.
/// Categories are not standalone aggregate roots — they are referenced by ID from
/// Transactions and RecurringFlows.
/// </summary>
public sealed class Category : IEntity
{
    private Guid _id;

    public CategoryId CategoryId => CategoryId.From(_id);
    public Guid Id => _id;
    public TenantId TenantId { get; private set; }
    public CategoryPath Path { get; private set; }
    public string DisplayName { get; private set; }
    public bool IsSystem { get; private set; }
    public CategoryId? ParentId { get; private set; }

    private Category()
    {
        // EF Core parameterless constructor — fields initialised by EF.
        // Not for direct use outside of EF hydration.
        Path = null!;
        DisplayName = null!;
    }

    public Category(
        CategoryId id,
        TenantId tenantId,
        CategoryPath path,
        string displayName,
        bool isSystem = false,
        CategoryId? parentId = null)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new EmptyStringException(nameof(displayName));
        }

        _id = id.Value;
        TenantId = tenantId;
        Path = path;
        DisplayName = displayName.Trim();
        IsSystem = isSystem;
        ParentId = parentId;
    }

    public static Category Create(
        TenantId tenantId,
        CategoryPath path,
        string displayName,
        CategoryId? parentId = null)
        => new(CategoryId.New(), tenantId, path, displayName, false, parentId);

    public void Rename(CategoryPath newPath, string newDisplayName)
    {
        if (IsSystem)
        {
            throw new InvalidOperationException("System categories cannot be renamed.");
        }

        if (string.IsNullOrWhiteSpace(newDisplayName))
        {
            throw new EmptyStringException(nameof(newDisplayName));
        }

        Path = newPath;
        DisplayName = newDisplayName.Trim();
    }
}
