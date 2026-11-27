using FlowLedger.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Persistence;

/// <summary>
/// Reload-and-retry for background pipelines (#157). Once rows carry an optimistic-concurrency token, a pipeline that loads rows,
/// changes them and writes them back (categorization, rules, merchant aliases, enrichment, duplicate merge, recurring detection)
/// can lose a race against a user edit. That is not an error: the unit is re-run against fresh data, which re-evaluates every
/// rule (so a category the user just set by hand is respected, never overwritten) and converges. Never use it around work that
/// cannot be repeated cheaply and safely (provider calls); those reconcile conflicts column by column instead.
/// </summary>
public static partial class ConcurrencyRetry
{
    /// <summary>Runs per unit before the conflict is allowed to surface.</summary>
    public const int DefaultAttempts = 3;

    public static async Task<T> RunAsync<T>(
        DbContext db, Func<Task<T>> unit, ILogger logger, string operation, CancellationToken ct, int maxAttempts = DefaultAttempts)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await unit();
            }
            catch (Exception ex) when (IsConflict(ex) && attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                LogARowChangedUnderneathIt(logger, ex, operation, attempt, maxAttempts);

                // Everything tracked is stale (and the failed batch is in an unknown state): start from a clean slate so the retry reads fresh rows.
                db.ChangeTracker.Clear();
            }
        }
    }

    public static async Task RunAsync(
        DbContext db, Func<Task> unit, ILogger logger, string operation, CancellationToken ct, int maxAttempts = DefaultAttempts)
        => await RunAsync<bool>(db, async () =>
        {
            await unit();
            return true;
        }, logger, operation, ct, maxAttempts);

    private static bool IsConflict(Exception ex) => ex is DbUpdateConcurrencyException or ConcurrencyConflictException;

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Operation}: a row changed underneath it (attempt {Attempt} of {Max}); reloading and running it again.")]
    private static partial void LogARowChangedUnderneathIt(ILogger logger, Exception exception, string? operation, int attempt, int max);
}
