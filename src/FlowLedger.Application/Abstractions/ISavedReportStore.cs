using FlowLedger.Application.Features.Query;

namespace FlowLedger.Application.Abstractions;

/// <summary>Persistence for user-saved views and reports (tenant-scoped).</summary>
public interface ISavedReportStore
{
    Task<IReadOnlyList<SavedReportDto>> ListAsync(CancellationToken ct = default);
    Task<SavedReportDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task<SavedReportDto> CreateAsync(SaveReportRequest request, CancellationToken ct = default);
    Task<SavedReportDto?> UpdateAsync(Guid id, SaveReportRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
