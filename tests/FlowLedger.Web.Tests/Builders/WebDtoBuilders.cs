using FlowLedger.Web.ApiClient;

namespace FlowLedger.Web.Tests.Builders;

// Builders for the Web client DTOs the Web tests construct (#173). When a DTO gains a field, only its builder changes.

/// <summary>Builds <see cref="CategoryRestoreDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class CategoryRestoreDtoBuilder
{
    private Guid _transactionId = Guid.NewGuid();
    private Guid? _categoryId;
    private bool _manual = true;
    private double? _confidence;

    public static CategoryRestoreDtoBuilder A() => new();

    public CategoryRestoreDtoBuilder WithTransactionId(Guid transactionId) { _transactionId = transactionId; return this; }
    public CategoryRestoreDtoBuilder WithCategoryId(Guid? categoryId) { _categoryId = categoryId; return this; }
    public CategoryRestoreDtoBuilder WithManual(bool manual) { _manual = manual; return this; }
    public CategoryRestoreDtoBuilder WithConfidence(double? confidence) { _confidence = confidence; return this; }

    public CategoryRestoreDto Build() => new(_transactionId, _categoryId, _manual, _confidence);

    public static implicit operator CategoryRestoreDto(CategoryRestoreDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="ConnectionIssueDto"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class ConnectionIssueDtoBuilder
{
    private string _kind = "ProviderUnavailable";
    private string? _message;
    private bool _retryable = true;
    private bool _requiresUserAction;
    private DateTimeOffset? _retryAfter;
    private DateTimeOffset? _at;
    private int _consecutiveFailures;
    private string? _providerCode;
    private string? _repairRef;

    public static ConnectionIssueDtoBuilder A() => new();

    public ConnectionIssueDtoBuilder WithKind(string kind) { _kind = kind; return this; }
    public ConnectionIssueDtoBuilder WithMessage(string? message) { _message = message; return this; }
    public ConnectionIssueDtoBuilder WithRetryable(bool retryable) { _retryable = retryable; return this; }
    public ConnectionIssueDtoBuilder WithRequiresUserAction(bool requiresUserAction) { _requiresUserAction = requiresUserAction; return this; }
    public ConnectionIssueDtoBuilder WithRetryAfter(DateTimeOffset? retryAfter) { _retryAfter = retryAfter; return this; }
    public ConnectionIssueDtoBuilder WithAt(DateTimeOffset? at) { _at = at; return this; }
    public ConnectionIssueDtoBuilder WithConsecutiveFailures(int consecutiveFailures) { _consecutiveFailures = consecutiveFailures; return this; }
    public ConnectionIssueDtoBuilder WithProviderCode(string? providerCode) { _providerCode = providerCode; return this; }
    public ConnectionIssueDtoBuilder WithRepairRef(string? repairRef) { _repairRef = repairRef; return this; }

    public ConnectionIssueDto Build() => new(_kind, _message, _retryable, _requiresUserAction, _retryAfter, _at, _consecutiveFailures, _providerCode, _repairRef);

    public static implicit operator ConnectionIssueDto(ConnectionIssueDtoBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="ProblemExtensions"/>; every member has a default, so a test names only what it cares about.</summary>
public sealed class ProblemExtensionsBuilder
{
    private string? _issueKind = "ReauthRequired";
    private bool _retryable;
    private bool _requiresUserAction = true;
    private int? _retryAfterSeconds = 120;
    private string? _provider = "Quiltt";

    public static ProblemExtensionsBuilder A() => new();

    public ProblemExtensionsBuilder WithIssueKind(string? issueKind) { _issueKind = issueKind; return this; }
    public ProblemExtensionsBuilder WithRetryable(bool retryable) { _retryable = retryable; return this; }
    public ProblemExtensionsBuilder WithRequiresUserAction(bool requiresUserAction) { _requiresUserAction = requiresUserAction; return this; }
    public ProblemExtensionsBuilder WithRetryAfterSeconds(int? retryAfterSeconds) { _retryAfterSeconds = retryAfterSeconds; return this; }
    public ProblemExtensionsBuilder WithProvider(string? provider) { _provider = provider; return this; }

    public ProblemExtensions Build() => new(_issueKind, _retryable, _requiresUserAction, _retryAfterSeconds, _provider);

    public static implicit operator ProblemExtensions(ProblemExtensionsBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="ConnectorLinkDto"/>: a healthy "Bank" link made at a fixed instant.</summary>
public sealed class ConnectorLinkDtoBuilder
{
    private static readonly DateTimeOffset At = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private string _institution = "Bank";
    private string _status = "ok";
    private DateTimeOffset _linkedAt = At;
    private DateTimeOffset _updatedAt = At;
    private ConnectionIssueDto? _issue;

    public static ConnectorLinkDtoBuilder A() => new();

    public ConnectorLinkDtoBuilder WithInstitution(string institution) { _institution = institution; return this; }
    public ConnectorLinkDtoBuilder WithStatus(string status) { _status = status; return this; }
    public ConnectorLinkDtoBuilder LinkedAt(DateTimeOffset linkedAt) { _linkedAt = linkedAt; return this; }
    public ConnectorLinkDtoBuilder UpdatedAt(DateTimeOffset updatedAt) { _updatedAt = updatedAt; return this; }
    public ConnectorLinkDtoBuilder WithIssue(ConnectionIssueDto? issue) { _issue = issue; return this; }

    public ConnectorLinkDto Build() => new(_institution, _status, _linkedAt, _updatedAt, _issue);

    public static implicit operator ConnectorLinkDto(ConnectorLinkDtoBuilder builder) => builder.Build();
}
