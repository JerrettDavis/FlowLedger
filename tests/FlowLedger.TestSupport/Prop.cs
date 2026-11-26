using CsCheck;

namespace FlowLedger.TestSupport;

/// <summary>
/// Thin wrapper over CsCheck so every property test runs the same way: a fixed starting seed (so a CI run is
/// reproducible), 1000 cases by default, and CsCheck's failure message, which prints the exact seed to replay.
/// Single-threaded on purpose: FluentAssertions assertion scopes are not safe to share across CsCheck worker threads. To replay a failure, copy the printed seed into the <c>seed</c> argument of the failing call (the suite-wide
/// default is <see cref="DefaultSeed"/>).
/// </summary>
public static class Prop
{
    /// <summary>Starting seed for every property; iteration N uses a seed derived from it, so runs are deterministic.</summary>
    public const string DefaultSeed = "0N0XIzNsQ0O2";

    public const int DefaultIterations = 1000;

    /// <summary>Asserts <paramref name="property"/> (throws on violation) for generated values.</summary>
    public static void ForAll<T>(Gen<T> gen, Action<T> property, string? seed = null, int iterations = DefaultIterations) =>
        gen.Sample(property, seed: seed ?? DefaultSeed, iter: iterations, threads: 1);

    /// <summary>Asserts <paramref name="property"/> returns true for generated values.</summary>
    public static void ForAll<T>(Gen<T> gen, Func<T, bool> property, string? seed = null, int iterations = DefaultIterations) =>
        gen.Sample(property, seed: seed ?? DefaultSeed, iter: iterations, threads: 1);
}
