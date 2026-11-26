namespace FlowLedger.Application.Features.BillSheet;

/// <summary>A user-correctable problem with a sheet request. Maps to HTTP 400.</summary>
public sealed class SheetOperationException(string message) : Exception(message);

