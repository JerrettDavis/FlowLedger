namespace FlowLedger.E2E.Tests;

/// <summary>
/// Shared gate for the E2E attributes: a test needs a running stack (<c>E2E_BASE_URL</c>). Without one it is reported as
/// <b>Skipped</b> with a reason, never as Passed. CI sets <c>E2E_REQUIRED=true</c>, which removes the skip so a missing or
/// renamed variable fails the run (<see cref="E2ETestBase"/> throws in that case) instead of going green with no assertions.
/// </summary>
internal static class E2EGate
{
    internal const string SkipReason =
        "E2E stack not configured: set E2E_BASE_URL (and E2E_API_URL/E2E_API_KEY) to run against a running FlowLedger stack. "
        + "CI sets E2E_REQUIRED=true so this can never skip there.";

    internal static string? SkipReasonOrNull()
    {
        var required = string.Equals(Environment.GetEnvironmentVariable("E2E_REQUIRED"), "true", StringComparison.OrdinalIgnoreCase);
        return required || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("E2E_BASE_URL")) ? null : SkipReason;
    }
}

/// <summary>A <see cref="FactAttribute"/> that skips (visibly) when no E2E stack is configured.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute() => Skip = E2EGate.SkipReasonOrNull();
}

/// <summary>A <see cref="TheoryAttribute"/> that skips (visibly) when no E2E stack is configured.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class E2ETheoryAttribute : TheoryAttribute
{
    public E2ETheoryAttribute() => Skip = E2EGate.SkipReasonOrNull();
}

/// <summary>
/// An E2E test that also needs a local Ollama model (probed at discovery). Without one it is Skipped with that reason, instead of
/// returning early and being reported as Passed. CI has no model, so these report as skipped there (allow-listed by name in the workflow).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class E2ELocalModelFactAttribute : FactAttribute
{
    public E2ELocalModelFactAttribute()
    {
        Skip = E2EGate.SkipReasonOrNull() ?? (ModelAvailable() ? null : "No local model: Ollama is not answering on http://localhost:11434, so there is nothing to validate.");
    }

    private static bool ModelAvailable()
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            return probe.GetAsync("http://localhost:11434/api/tags").GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
