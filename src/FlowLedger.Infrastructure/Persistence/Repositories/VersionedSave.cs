using FlowLedger.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

/// <summary>
/// Saves a tracked change and turns a lost optimistic-concurrency race (#157) into <see cref="ConcurrencyConflictException"/>.
/// A unique violation on insert means a concurrent writer created the same key first, which is the same kind of conflict.
/// </summary>
internal static class VersionedSave
{
    public static async Task SaveAsync(FlowLedgerDbContext db, string noun, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException($"This {noun} was changed elsewhere. Reload it and try again.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConcurrencyConflictException($"This {noun} was changed elsewhere. Reload it and try again.", ex);
        }
    }
}
