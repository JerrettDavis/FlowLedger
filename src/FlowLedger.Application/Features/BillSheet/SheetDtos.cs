namespace FlowLedger.Application.Features.BillSheet;

/// <summary>A user-correctable problem with a sheet request. Maps to HTTP 400.</summary>
public sealed class SheetOperationException(string message, string? field = null) : Exception(message)
{
    /// <summary>The request field the message is about (camelCase), so a form can show it next to that field; null when it is not tied to one.</summary>
    public string? Field { get; } = field;
}

