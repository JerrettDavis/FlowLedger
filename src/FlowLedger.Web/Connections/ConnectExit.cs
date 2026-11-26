using MudBlazor;

namespace FlowLedger.Web.Connections;

/// <summary>How an embedded "link a bank" widget closed. Any connector's interop normalises its own exit events to these three.</summary>
public enum ConnectOutcome
{
    Success,
    Cancelled,
    Failed,
}

/// <summary>
/// One place that turns a widget exit into user-facing text. Some failure modes (aggregator down, timeouts, bad credentials at the
/// bank) happen only inside a hosted widget and leave NO server-side state, so this exit is the only signal the app gets: the message
/// must say nothing was linked and what to do next, without blaming the user or leaking a vendor name.
/// </summary>
public static class ConnectExit
{
    public static ConnectOutcome Parse(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "success" => ConnectOutcome.Success,
        "cancelled" or "canceled" or "abort" or "aborted" => ConnectOutcome.Cancelled,
        _ => ConnectOutcome.Failed,
    };

    /// <summary>The snackbar for an exit; null when no message is wanted.</summary>
    public static (string Message, Severity Severity)? Message(ConnectOutcome outcome) => outcome switch
    {
        ConnectOutcome.Success => ("Bank linked. Syncing your accounts…", Severity.Success),
        ConnectOutcome.Cancelled => ("Bank linking was cancelled. Nothing was changed.", Severity.Info),
        _ => ("The bank connection didn't complete, so nothing was linked and your existing accounts are unchanged. " +
              "This is often temporary (the bank or the connection service may be busy). Try again in a few minutes, or try a different bank.",
              Severity.Warning),
    };
}
