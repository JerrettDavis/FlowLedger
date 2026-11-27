namespace FlowLedger.Application.Abstractions;

/// <summary>
/// "This sync would make N provider calls" (#65). A sync lists the connection's accounts once and then reads one page of
/// transactions per active account (more pages only when a cursor is far behind, so this is the typical, not the worst, case).
/// </summary>
public static class SyncCallEstimate
{
    public static int ForAccounts(int activeAccounts, int pagesPerAccount = 1)
        => activeAccounts <= 0 ? 0 : 1 + (activeAccounts * Math.Max(1, pagesPerAccount));
}
