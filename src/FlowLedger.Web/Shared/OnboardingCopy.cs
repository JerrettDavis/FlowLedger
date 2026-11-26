namespace FlowLedger.Web.Shared;

/// <summary>
/// Empty-state instructions that match the top-bar action the current environment actually shows:
/// "Connect bank" in production, "Connect &amp; Sync" (simulated data) in demo and sandbox.
/// </summary>
public static class OnboardingCopy
{
    /// <summary>How to get data into an empty screen, e.g. "Use Connect bank in the top bar to link an institution, then run a forecast."</summary>
    public static string LoadData(string? environment, string? then = null)
    {
        var body = environment switch
        {
            "demo" => "Use Connect & Sync in the top bar to load demo data",
            "sandbox" => "Use Connect & Sync in the top bar to load sandbox data",
            "production" => "Use Connect bank in the top bar to link an institution (owner only)",
            _ => "Connect a data source from the top bar, or add records by hand",
        };
        return string.IsNullOrWhiteSpace(then) ? body + "." : $"{body}, {then}.";
    }
}
