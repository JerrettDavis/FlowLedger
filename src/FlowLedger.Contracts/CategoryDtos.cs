namespace FlowLedger.Contracts;

/// <summary>Response DTO for a single category.</summary>
public sealed record CategoryDto(
    Guid Id,
    string Path,
    string DisplayName,
    bool IsSystem,
    Guid? ParentId,
    uint Version = 0);

/// <summary>Request DTO for creating a category.</summary>
public sealed record CreateCategoryRequest(
    string Path,
    string DisplayName,
    Guid? ParentId);

/// <summary>Request DTO for renaming a category.</summary>
public sealed record UpdateCategoryRequest(
    string Path,
    string DisplayName);
