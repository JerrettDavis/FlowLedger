using FlowLedger.Application.Features.HomeAssistant;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

/// <summary>
/// Per-tenant Home Assistant sharing settings plus the "already sent" ledger, through EF (#154). The interface still takes a
/// tenant id; it must be the current tenant, otherwise the call fails loudly. The webhook URL is a secret: encrypted at rest with
/// AES-256-GCM (v2 envelope bound to tenant and purpose, #156) using <c>Secrets:EncryptionKey</c> / <c>HomeAssistant:EncryptionKey</c> (base64, 32 bytes), refused when no key is configured and never returned
/// by any read model. The ledger stores only opaque dedupe keys, never amounts.
/// </summary>
internal sealed class EfHomeAssistantStore : IHomeAssistantStore
{
    private readonly FlowLedgerDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly SecretKeyRing _ring;

    internal const string Purpose = "home-assistant-webhook";

    public EfHomeAssistantStore(FlowLedgerDbContext db, ITenantContext tenant, string? base64Key = null, SecretKeyRing? ring = null)
    {
        _db = db;
        _tenant = tenant;
        _ring = ring ?? SecretKeyRing.From(base64Key);
    }

    public bool CanStoreWebhook => _ring.CanSeal;

    private string? OpenWebhook(Guid tenantId, string stored) => SecretBox.Open(stored, _ring, SecretBox.Aad(tenantId, Purpose));

    private void Guard(Guid tenantId)
    {
        if (tenantId != _tenant.TenantId)
        {
            throw new InvalidOperationException("Home Assistant settings can only be accessed for the current tenant.");
        }
    }

    public async Task<HomeAssistantSettings> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        Guard(tenantId);
        var r = await _db.HomeAssistantSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (r is null)
        {
            return HomeAssistantSettings.Default;
        }

        var url = r.Webhook is null ? null : OpenWebhook(tenantId, r.Webhook);
        var kinds = r.Kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(HaEventKinds.All.Contains).ToList();
        return new HomeAssistantSettings(
            r.Enabled, r.Webhook is not null, HomeAssistantWebhookUrl.DisplayHost(url), r.ShareAmounts, r.IncludeNames, r.LeadDays,
            kinds.Count == 0 ? HaEventKinds.All : kinds, r.LastSentUtc, r.LastError);
    }

    public async Task<string?> GetWebhookUrlAsync(Guid tenantId, CancellationToken ct = default)
    {
        Guard(tenantId);
        var stored = await _db.HomeAssistantSettings.AsNoTracking().Select(r => r.Webhook).FirstOrDefaultAsync(ct);
        return stored is null ? null : OpenWebhook(tenantId, stored);
    }

    public async Task SaveAsync(Guid tenantId, bool enabled, string? webhookUrl, bool clearWebhook, bool shareAmounts, bool includeNames,
        int leadDays, IReadOnlyList<string> kinds, CancellationToken ct = default)
    {
        Guard(tenantId);
        if (webhookUrl is not null && !_ring.CanSeal)
        {
            throw new InvalidOperationException("Set HomeAssistant:EncryptionKey before storing a webhook URL.");
        }

        var sealedUrl = webhookUrl is null ? null : SecretBox.SealV2(webhookUrl, _ring, SecretBox.Aad(tenantId, Purpose));
        var hasWebhook = webhookUrl is not null;
        var kindsText = string.Join(',', kinds);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO home_assistant_settings (tenant_id, enabled, webhook, share_amounts, include_names, lead_days, kinds)
               VALUES ({tenantId}, {enabled}, {sealedUrl}, {shareAmounts}, {includeNames}, {leadDays}, {kindsText})
               ON CONFLICT (tenant_id) DO UPDATE SET enabled = EXCLUDED.enabled,
               webhook = CASE WHEN {clearWebhook} THEN NULL WHEN {hasWebhook} THEN EXCLUDED.webhook ELSE home_assistant_settings.webhook END,
               share_amounts = EXCLUDED.share_amounts, include_names = EXCLUDED.include_names, lead_days = EXCLUDED.lead_days,
               kinds = EXCLUDED.kinds, updated_utc = now()", ct);
    }

    public async Task<bool> TryClaimAsync(Guid tenantId, string dedupeKey, CancellationToken ct = default)
    {
        Guard(tenantId);
        return await _db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO home_assistant_sent (tenant_id, dedupe_key) VALUES ({tenantId}, {dedupeKey}) ON CONFLICT DO NOTHING", ct) == 1;
    }

    public async Task ReleaseAsync(Guid tenantId, string dedupeKey, CancellationToken ct = default)
    {
        Guard(tenantId);
        await _db.HomeAssistantSent.Where(s => s.DedupeKey == dedupeKey).ExecuteDeleteAsync(ct);
    }

    public async Task RecordResultAsync(Guid tenantId, string? error, CancellationToken ct = default)
    {
        Guard(tenantId);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE home_assistant_settings SET last_error = {error},
               last_sent_utc = CASE WHEN {error}::text IS NULL THEN now() ELSE last_sent_utc END WHERE tenant_id = {tenantId}", ct);
    }

    public async Task PruneAsync(Guid tenantId, DateTimeOffset olderThan, CancellationToken ct = default)
    {
        Guard(tenantId);
        await _db.HomeAssistantSent.Where(s => s.SentUtc < olderThan).ExecuteDeleteAsync(ct);
    }
}
