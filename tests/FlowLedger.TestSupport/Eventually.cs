namespace FlowLedger.TestSupport;

/// <summary>
/// The one sanctioned place for polling in tests: wait for a condition on code that gives us no completion signal
/// (for example a fire-and-forget background task). Prefer a <see cref="TaskCompletionSource"/> gate whenever the code
/// under test lets you observe progress; use this only when it does not. The timeout is a generous safety net for a
/// loaded CI runner, not a tuned delay: a passing run returns as soon as the condition holds.
/// </summary>
public static class Eventually
{
    public static async Task WaitAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > limit)
            {
                throw new TimeoutException($"Condition was not met in time: {because}");
            }

            await Task.Delay(5);
        }
    }
}
