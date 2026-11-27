namespace FlowLedger.Application.Abstractions;

// Typed application failures (#90). Each maps to one HTTP status in the API's central exception handler, so endpoints do
// not catch BCL exceptions. They derive from the BCL type they replace so any non-HTTP caller that still catches the
// broad type keeps working; an unexpected raw InvalidOperationException/ArgumentException is NOT mapped and stays a 500.

/// <summary>The requested entity does not exist for the caller (404).</summary>
public class NotFoundException(string message) : KeyNotFoundException(message);

/// <summary>A request value breaks a rule; reported as a 400 validation problem keyed by <see cref="Field"/>.</summary>
public class RequestValidationException(string field, string message) : ArgumentException(message)
{
    public string Field { get; } = field;
}

/// <summary>The request conflicts with the current state of the resource (409).</summary>
public class ConflictException(string message, string? title = null) : InvalidOperationException(message)
{
    public string? Title { get; } = title;
}

/// <summary>The request was understood but cannot be processed, e.g. a malformed import file (422).</summary>
public class UnprocessableRequestException(string message, string title) : InvalidOperationException(message)
{
    public string Title { get; } = title;
}
