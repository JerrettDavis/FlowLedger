using FlowLedger.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence;

internal sealed class EfRowVersionGuard(FlowLedgerDbContext db) : IRowVersionGuard
{
    public void Expect(object entity, uint? expectedVersion)
    {
        if (expectedVersion is not { } version)
        {
            return;
        }

        var entry = db.Entry(entity);
        entry.Property("RowVersion").OriginalValue = version;

        // The precondition must hold even when only child rows change (a transaction's splits live in their own table) or when the
        // write turns out to be a no-op, so make the parent row take part in the save: an UPDATE of an unchanged column is what
        // carries the "WHERE xmin = ..." check. (Setting the whole entry Modified is not enough: change detection resets it.)
        var carrier = entry.Properties.FirstOrDefault(p =>
            !p.Metadata.IsKey() && !p.Metadata.IsConcurrencyToken && p.Metadata.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never);
        if (carrier is not null)
        {
            carrier.IsModified = true;
        }
    }
}
