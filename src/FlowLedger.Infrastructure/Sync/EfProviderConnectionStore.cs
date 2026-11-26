using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Entities;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Sync;

internal sealed class EfProviderConnectionStore(FlowLedgerDbContext db, ITenantContext tenant) : IProviderConnectionStore
{
    internal const int MaxMessageLength = 500;

    public async Task<IReadOnlyList<ProviderConnectionInfo>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.ProviderConnections.AsNoTracking()
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);
        return rows.Select(c => new ProviderConnectionInfo(
            c.ProviderName, c.Institution, c.Status, c.CreatedAt, c.UpdatedAt,
            c.IssueKind is null
                ? null
                : new ConnectionIssueInfo(
                    c.IssueKind, c.IssueMessage ?? string.Empty, c.IssueRetryable, c.IssueRequiresUserAction,
                    c.IssueRetryAfter, c.IssueAt ?? c.UpdatedAt, c.ConsecutiveFailures, c.IssueProviderCode, c.IssueRepairRef, c.IssueInformational))).ToList();
    }

    public async Task<string?> GetMemberProviderIdAsync(string providerName, CancellationToken ct = default)
        => (await db.ProviderConnections.FirstOrDefaultAsync(c => c.ProviderName == providerName, ct))?.MemberProviderId;

    /// <summary>
    /// Set-based update on purpose: it never touches the change tracker, so a failed sync's half-built, unsaved entities
    /// are not flushed as a side effect of recording the failure, and the failure counter increments atomically.
    /// No-op when the tenant has no connection for the provider.
    /// </summary>
    public async Task RecordHealthAsync(string providerName, ConnectionIssueInfo? issue, CancellationToken ct = default)
    {
        var rows = db.ProviderConnections.Where(c => c.ProviderName == providerName);
        if (issue is null)
        {
            await rows.ExecuteUpdateAsync(s => s
                .SetProperty(c => c.IssueKind, (string?)null)
                .SetProperty(c => c.IssueMessage, (string?)null)
                .SetProperty(c => c.IssueRetryable, false)
                .SetProperty(c => c.IssueRequiresUserAction, false)
                .SetProperty(c => c.IssueRetryAfter, (DateTimeOffset?)null)
                .SetProperty(c => c.IssueAt, (DateTimeOffset?)null)
                .SetProperty(c => c.IssueProviderCode, (string?)null)
                .SetProperty(c => c.IssueRepairRef, (string?)null)
                .SetProperty(c => c.IssueInformational, false)
                .SetProperty(c => c.ConsecutiveFailures, 0), ct);
            return;
        }

        var message = issue.Message.Length > MaxMessageLength ? issue.Message[..MaxMessageLength] : issue.Message;
        await rows.ExecuteUpdateAsync(s => s
            .SetProperty(c => c.IssueKind, issue.Kind)
            .SetProperty(c => c.IssueMessage, message)
            .SetProperty(c => c.IssueRetryable, issue.Retryable)
            .SetProperty(c => c.IssueRequiresUserAction, issue.RequiresUserAction)
            .SetProperty(c => c.IssueRetryAfter, issue.RetryAfter)
            .SetProperty(c => c.IssueAt, issue.At)
            .SetProperty(c => c.IssueProviderCode, issue.ProviderCode)
            .SetProperty(c => c.IssueRepairRef, issue.RepairRef)
            .SetProperty(c => c.IssueInformational, issue.Informational)
            // An informational issue (reads still succeed) is not a failed attempt, so it never advances the give-up counter.
            .SetProperty(c => c.ConsecutiveFailures, c => issue.Informational ? 0 : c.ConsecutiveFailures + 1), ct);
    }

    public async Task SaveAsync(string providerName, string memberProviderId, string institution, string status, CancellationToken ct = default)
    {
        if (await db.ProviderConnections.AnyAsync(c => c.ProviderName == providerName, ct))
        {
            return;
        }

        await db.ProviderConnections.AddAsync(
            ProviderConnectionRecord.Create(tenant.TenantId, providerName, memberProviderId, institution, status, DateTimeOffset.UtcNow), ct);
        await db.SaveChangesAsync(ct);
    }
}
