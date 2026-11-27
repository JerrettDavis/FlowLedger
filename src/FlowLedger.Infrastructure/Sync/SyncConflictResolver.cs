using FlowLedger.Domain.Aggregates;
using FlowLedger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>
/// Reconciles a lost optimistic-concurrency race inside a sync (#157). A sync cannot simply be re-run (the provider calls are
/// already paid for), so a conflicting row is merged instead:
/// <list type="bullet">
/// <item>Rows the sync only touches in columns the user cannot edit (a pending transaction settling, a stale hold retired):
/// the stored version token is adopted and the sync's changed columns are written; the user's columns are untouched, so
/// their edit survives.</item>
/// <item>Accounts are merged the same way, except that a type the user locked in the meantime is never overwritten by the
/// sync's reclassification (the sync decided from a copy that predated the lock).</item>
/// <item>A row deleted in the meantime is forgotten.</item>
/// </list>
/// </summary>
internal sealed partial class SyncConflictResolver(FlowLedgerDbContext db, ILogger logger)
{
    /// <summary>Installs this resolver on the context until the returned scope is disposed.</summary>
    public IDisposable Install()
    {
        var previous = db.ConcurrencyConflictResolver;
        db.ConcurrencyConflictResolver = ResolveAsync;
        return new Restore(db, previous);
    }

    private async Task<bool> ResolveAsync(DbUpdateConcurrencyException ex, CancellationToken ct)
    {
        foreach (var entry in ex.Entries)
        {
            if (entry.Metadata.IsOwned())
            {
                continue; // an owned value shares its owner's row; the owner's entry carries the token
            }

            var token = entry.Properties.FirstOrDefault(p => p.Metadata.IsConcurrencyToken);
            if (token is null)
            {
                return false; // not a conflict this resolver understands
            }

            var current = await entry.GetDatabaseValuesAsync(ct);
            if (current is null)
            {
                LogWasDeletedWhileTheSync(logger, entry.Metadata.ClrType.Name);
                entry.State = EntityState.Detached;
                continue;
            }

            // Write our changed columns over the newer row. Unchanged columns are not part of the UPDATE, so edits made to them survive.
            token.OriginalValue = current[token.Metadata];
            KeepUserLockedType(entry, current);
            LogChangedWhileTheSyncWas(logger, entry.Metadata.ClrType.Name);
        }

        return true;
    }

    /// <summary>
    /// The sync decides to reclassify an account from a copy that may predate the user's own choice. If the stored row says the user
    /// locked the type, the sync's type change is dropped (the same rule <see cref="Account.ChangeType"/> applies on a fresh copy).
    /// </summary>
    private static void KeepUserLockedType(EntityEntry entry, PropertyValues current)
    {
        if (entry.Entity is not Account)
        {
            return;
        }

        var lockedInStore = current[nameof(Account.TypeLockedByUser)] is true;
        var type = entry.Property(nameof(Account.AccountType));
        if (lockedInStore && type.IsModified)
        {
            type.CurrentValue = current[nameof(Account.AccountType)];
            type.OriginalValue = current[nameof(Account.AccountType)];
            type.IsModified = false;
        }
    }

    private sealed class Restore(FlowLedgerDbContext db, Func<DbUpdateConcurrencyException, CancellationToken, Task<bool>>? previous) : IDisposable
    {
        public void Dispose() => db.ConcurrencyConflictResolver = previous;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Entity} was deleted while the sync was updating it; skipping it.")]
    private static partial void LogWasDeletedWhileTheSync(ILogger logger, string? entity);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Entity} changed while the sync was updating it; merged the sync's columns into the newer row.")]
    private static partial void LogChangedWhileTheSyncWas(ILogger logger, string? entity);
}
