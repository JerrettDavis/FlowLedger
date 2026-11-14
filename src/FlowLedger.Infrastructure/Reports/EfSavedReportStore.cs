using System.Text.Json;
using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Query;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Entities;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Reports;

internal sealed class EfSavedReportStore(FlowLedgerDbContext db, ITenantContext tenant) : ISavedReportStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SavedReportDto>> ListAsync(CancellationToken ct = default)
        => (await db.SavedReports.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<SavedReportDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var r = await db.SavedReports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return r is null ? null : ToDto(r);
    }

    public async Task<SavedReportDto> CreateAsync(SaveReportRequest request, CancellationToken ct = default)
    {
        var record = SavedReportRecord.Create(
            tenant.TenantId, request.Name.Trim(), request.Description?.Trim(), request.Kind,
            JsonSerializer.Serialize(request.Panels, Json), DateTimeOffset.UtcNow);
        await db.SavedReports.AddAsync(record, ct);
        await db.SaveChangesAsync(ct);
        return ToDto(record);
    }

    public async Task<SavedReportDto?> UpdateAsync(Guid id, SaveReportRequest request, CancellationToken ct = default)
    {
        var record = await db.SavedReports.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (record is null)
        {
            return null;
        }

        record.Update(request.Name.Trim(), request.Description?.Trim(), request.Kind,
            JsonSerializer.Serialize(request.Panels, Json), DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return ToDto(record);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var record = await db.SavedReports.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (record is null)
        {
            return false;
        }

        db.SavedReports.Remove(record);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static SavedReportDto ToDto(SavedReportRecord r)
        => new(r.Id.ToString("D"), r.Name, r.Description, r.Kind, false,
            JsonSerializer.Deserialize<List<ReportPanel>>(r.PanelsJson, Json) ?? [], r.CreatedAt, r.UpdatedAt);
}
