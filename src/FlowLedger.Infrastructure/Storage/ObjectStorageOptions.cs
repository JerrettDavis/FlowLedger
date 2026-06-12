namespace FlowLedger.Infrastructure.Storage;

/// <summary>
/// Configuration for the local-disk object storage implementation.
/// Bind from the "ObjectStorage" configuration section.
/// </summary>
public sealed class ObjectStorageOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ObjectStorage";

    /// <summary>
    /// Root directory under which all objects are stored.
    /// Defaults to <c>/data/objects</c> (container-friendly path).
    /// Override in <c>appsettings.Development.json</c> or environment variables for local runs.
    /// </summary>
    public string RootPath { get; set; } = "/data/objects";
}
