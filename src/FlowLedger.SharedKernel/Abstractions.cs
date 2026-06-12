namespace FlowLedger.SharedKernel;

/// <summary>
/// Marker interface for domain entities.
/// </summary>
public interface IEntity
{
    Guid Id { get; }
}

/// <summary>
/// Marker interface for aggregate roots. Aggregate roots maintain a collection of
/// uncommitted domain events that are dispatched after persistence.
/// </summary>
public interface IAggregateRoot : IEntity
{
    IReadOnlyList<IDomainEvent> DomainEvents { get; }
    void ClearDomainEvents();
}

/// <summary>
/// Marker interface for domain events. All events are immutable records.
/// </summary>
public interface IDomainEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// Tenant context abstraction. Implementation registered in Infrastructure.
/// </summary>
public interface ITenantContext
{
    Guid TenantId { get; }
    Guid UserId { get; }
}

/// <summary>
/// Object storage abstraction. Local-disk implementation in Infrastructure.
/// An S3/MinIO implementation can be substituted without changing callers.
/// </summary>
public interface IObjectStorage
{
    Task<Uri> UploadAsync(string key, Stream content, string contentType, CancellationToken ct = default);
    Task<Stream> DownloadAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}
