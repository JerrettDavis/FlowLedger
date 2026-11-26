namespace FlowLedger.TestSupport;

/// <summary>
/// A test against a real external service. Runs only when <paramref name="requiredEnvVar"/> is set (a scheduled job sets the
/// secret), otherwise it is reported as skipped with that reason. Replaces a hard-coded <c>Skip</c> that nobody ever removes.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LiveFactAttribute : Xunit.FactAttribute
{
    public LiveFactAttribute(string requiredEnvVar)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(requiredEnvVar)))
        {
            Skip = $"Live test: set {requiredEnvVar} (and the provider's other env vars) to run against the real sandbox.";
        }
    }
}
