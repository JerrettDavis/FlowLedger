namespace FlowLedger.Application.Features.Integrations;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum IntegrationKind
{
    /// <summary>Call a JSON/HTTP API on demand (base URL + path).</summary>
    Rest,

    /// <summary>Call a SOAP service described by a WSDL.</summary>
    Soap,

    /// <summary>Push signed FlowLedger events to a URL.</summary>
    Webhook,
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum IntegrationAuth
{
    None,
    Bearer,
    Basic,
    ApiKey,
}

/// <summary>Event kinds a webhook integration can subscribe to.</summary>
public static class IntegrationEvents
{
    public const string Test = "test";
    public const string ImportCompleted = "import.completed";
    public const string SyncCompleted = "sync.completed";
    public const string AlertRaised = "alert.raised";

    public static IReadOnlyList<string> All { get; } = [ImportCompleted, SyncCompleted, AlertRaised];
}

/// <summary>The non-secret part of an integration. Secrets (credential, signing secret) never live here.</summary>
public sealed record IntegrationConfig(
    string Url,
    IntegrationAuth Auth,
    string? AuthUser,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<string> Events,
    string? SoapEndpoint);

/// <summary>The read model. <c>HasCredential</c>/<c>HasSigningSecret</c> say a secret exists; the value is never returned.</summary>
public sealed record Integration(
    Guid Id,
    string Name,
    IntegrationKind Kind,
    bool Enabled,
    IntegrationConfig Config,
    bool HasCredential,
    bool HasSigningSecret,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastUsedUtc,
    string? LastError);

public sealed record IntegrationSecrets(string? Credential, string? SigningSecret);

public sealed record IntegrationDelivery(
    Guid Id,
    Guid IntegrationId,
    DateTimeOffset AtUtc,
    string Trigger,
    string Method,
    string Target,
    int? StatusCode,
    int DurationMs,
    string? Error);

public sealed record IntegrationSaveRequest(
    string Name,
    IntegrationKind Kind,
    bool Enabled,
    string Url,
    IntegrationAuth Auth,
    string? AuthUser,
    string? Credential,
    bool ClearCredential,
    Dictionary<string, string>? Headers,
    string[]? Events,
    string? SoapEndpoint);

public sealed record IntegrationSaveResult(
    Integration? Integration,
    IReadOnlyDictionary<string, string[]>? Errors,
    string? NewSigningSecret)
{
    public static IntegrationSaveResult Fail(string field, string error)
        => new(null, new Dictionary<string, string[]> { [field] = [error] }, null);
}

public sealed record RestCallRequest(string Method, string? Path, string? Query, Dictionary<string, string>? Headers, string? Body, string? ContentType);

public sealed record SoapInvokeRequest(string Operation, string BodyXml, string? Version = null);

/// <summary>The outcome of one outbound call, shown to the integration's owner.</summary>
public sealed record IntegrationCallResult(
    int? StatusCode,
    string? ContentType,
    string Body,
    bool Truncated,
    int DurationMs,
    string? Error);

/// <summary>An integration plus the operations a SOAP service offers.</summary>
public sealed record SoapOperationsResult(IReadOnlyList<WsdlOperation> Operations, string? Error);
