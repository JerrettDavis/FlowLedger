namespace FlowLedger.Web.Shared;

/// <summary>Which body a dashboard card shows: its own loading, error, empty or ready content.</summary>
public enum DashboardCardState
{
    Ready,
    Loading,
    Error,
    Empty,
}
