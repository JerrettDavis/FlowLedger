namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>A user-saved view (one panel) or report (several panels). Panels are stored as JSON.</summary>
internal sealed class SavedReportRecord
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string Kind { get; private set; } = "View";
    public string PanelsJson { get; private set; } = "[]";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private SavedReportRecord() { }

    public static SavedReportRecord Create(Guid tenantId, string name, string? description, string kind, string panelsJson, DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Description = description,
            Kind = kind,
            PanelsJson = panelsJson,
            CreatedAt = now,
            UpdatedAt = now,
        };

    public void Update(string name, string? description, string kind, string panelsJson, DateTimeOffset now)
    {
        Name = name;
        Description = description;
        Kind = kind;
        PanelsJson = panelsJson;
        UpdatedAt = now;
    }
}
