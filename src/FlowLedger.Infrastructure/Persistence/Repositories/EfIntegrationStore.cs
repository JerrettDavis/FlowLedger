using System.Text.Json;
using FlowLedger.Application.Features.Integrations;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Persistence.Entities;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

/// <summary>
/// Per-tenant integration definitions and their delivery log, through EF (#154). The interface still takes a tenant id; it must be
/// the current tenant, otherwise the call fails loudly. Credentials and webhook signing secrets are encrypted at rest with AES-256-GCM
/// (v2 envelope bound to tenant, purpose and row, #156; <c>Secrets:EncryptionKey</c>, then <c>Integrations:EncryptionKey</c>, then <c>HomeAssistant:EncryptionKey</c>); with no key they are refused rather than
/// stored in clear text. The delivery log holds status and timing only, never request or response bodies.
/// </summary>
internal sealed class EfIntegrationStore : IIntegrationStore
{
    private const int KeepDeliveries = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly FlowLedgerDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly SecretKeyRing _ring;

    internal const string Purpose = "integration-secrets";

    public EfIntegrationStore(FlowLedgerDbContext db, ITenantContext tenant, string? base64Key = null, SecretKeyRing? ring = null)
    {
        _db = db;
        _tenant = tenant;
        _ring = ring ?? SecretKeyRing.From(base64Key);
    }

    public bool CanStoreSecrets => _ring.CanSeal;

    private byte[] Aad(Guid integrationId) => SecretBox.Aad(_tenant.TenantId, Purpose, integrationId);

    private void Guard(Guid tenantId)
    {
        if (tenantId != _tenant.TenantId)
        {
            throw new InvalidOperationException("Integrations can only be accessed for the current tenant.");
        }
    }

    public async Task<IReadOnlyList<Integration>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        Guard(tenantId);
        // ToLower() is translated to SQL lower() by EF; the culture-aware analyzers do not apply to a server-side expression.
#pragma warning disable CA1304, CA1311
        var rows = await _db.Integrations.AsNoTracking().OrderBy(r => r.Name.ToLower()).ThenBy(r => r.CreatedUtc).ToListAsync(ct);
#pragma warning restore CA1304, CA1311
        return rows.Select(Read).ToList();
    }

    public async Task<Integration?> GetAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        Guard(tenantId);
        var row = await _db.Integrations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        return row is null ? null : Read(row);
    }

    public async Task<IntegrationSecrets> GetSecretsAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        Guard(tenantId);
        var stored = await _db.Integrations.AsNoTracking().Where(r => r.Id == id).Select(r => r.Secrets).FirstOrDefaultAsync(ct);
        return stored is null ? new IntegrationSecrets(null, null) : OpenSecrets(id, stored);
    }

    public async Task SaveAsync(Guid tenantId, Integration integration, string? credential, bool clearCredential, string? signingSecret, CancellationToken ct = default)
    {
        Guard(tenantId);
        if ((credential is not null || signingSecret is not null) && !_ring.CanSeal)
        {
            throw new InvalidOperationException("Set Integrations:EncryptionKey before storing integration secrets.");
        }

        var configJson = JsonSerializer.Serialize(integration.Config, Json);
        var kind = integration.Kind.ToString();
        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // The row lock serialises concurrent rotations so a merge never loses the other secret.
            var stored = (await _db.Database.SqlQuery<string>(
                $"SELECT secrets AS \"Value\" FROM integrations WHERE tenant_id = {tenantId} AND id = {integration.Id} AND secrets IS NOT NULL FOR UPDATE")
                .ToListAsync(ct)).FirstOrDefault();
            var current = stored is null ? new IntegrationSecrets(null, null) : OpenSecrets(integration.Id, stored);

            var next = new IntegrationSecrets(
                clearCredential ? null : credential ?? current.Credential,
                signingSecret ?? current.SigningSecret);
            var sealedSecrets = next is { Credential: null, SigningSecret: null } ? null : SealSecrets(integration.Id, next);

            await _db.Database.ExecuteSqlInterpolatedAsync(
                $@"INSERT INTO integrations (tenant_id, id, name, kind, enabled, config, secrets, created_utc)
                   VALUES ({tenantId}, {integration.Id}, {integration.Name}, {kind}, {integration.Enabled}, {configJson}, {sealedSecrets}, {integration.CreatedUtc})
                   ON CONFLICT (tenant_id, id) DO UPDATE SET name = EXCLUDED.name, enabled = EXCLUDED.enabled,
                   config = EXCLUDED.config, secrets = EXCLUDED.secrets", ct);
            await tx.CommitAsync(ct);
        });
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        Guard(tenantId);
        var rows = 0;
        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _db.IntegrationDeliveries.Where(d => d.IntegrationId == id).ExecuteDeleteAsync(ct);
            rows = await _db.Integrations.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        });
        return rows > 0;
    }

    public async Task RecordDeliveryAsync(Guid tenantId, IntegrationDelivery delivery, CancellationToken ct = default)
    {
        Guard(tenantId);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO integration_deliveries (tenant_id, id, integration_id, at_utc, trigger, method, target, status_code, duration_ms, error)
               VALUES ({tenantId}, {delivery.Id}, {delivery.IntegrationId}, {delivery.AtUtc}, {delivery.Trigger}, {delivery.Method},
               {delivery.Target}, {delivery.StatusCode}, {delivery.DurationMs}, {delivery.Error})", ct);

        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE integrations SET last_used_utc = {delivery.AtUtc}, last_error = {delivery.Error}
               WHERE tenant_id = {tenantId} AND id = {delivery.IntegrationId}", ct);

        var integrationId = delivery.IntegrationId;
        var keep = _db.IntegrationDeliveries.Where(d => d.IntegrationId == integrationId)
            .OrderByDescending(d => d.AtUtc).Take(KeepDeliveries).Select(d => d.Id);
        await _db.IntegrationDeliveries.Where(d => d.IntegrationId == integrationId && !keep.Contains(d.Id)).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<IntegrationDelivery>> ListDeliveriesAsync(Guid tenantId, Guid id, int limit, CancellationToken ct = default)
    {
        Guard(tenantId);
        var rows = await _db.IntegrationDeliveries.AsNoTracking()
            .Where(d => d.IntegrationId == id)
            .OrderByDescending(d => d.AtUtc)
            .Take(Math.Clamp(limit, 1, KeepDeliveries))
            .ToListAsync(ct);
        return rows.Select(d => new IntegrationDelivery(
            d.Id, d.IntegrationId, d.AtUtc, d.Trigger, d.Method, d.Target, d.StatusCode, d.DurationMs, d.Error)).ToList();
    }

    private Integration Read(IntegrationRecord r)
    {
        var config = JsonSerializer.Deserialize<IntegrationConfig>(r.Config, Json)
            ?? throw new InvalidOperationException("Integration config is unreadable.");
        var secrets = r.Secrets is null ? new IntegrationSecrets(null, null) : OpenSecrets(r.Id, r.Secrets);
        return new Integration(
            r.Id, r.Name, Enum.Parse<IntegrationKind>(r.Kind), r.Enabled, config,
            secrets.Credential is not null, secrets.SigningSecret is not null, r.CreatedUtc, r.LastUsedUtc, r.LastError);
    }

    private string SealSecrets(Guid id, IntegrationSecrets s) => SecretBox.SealV2(JsonSerializer.Serialize(s, Json), _ring, Aad(id));

    /// <summary>A value that cannot be decrypted (key changed or removed) reads as having no secrets, so nothing is sent with them.</summary>
    private IntegrationSecrets OpenSecrets(Guid id, string stored)
    {
        var plain = SecretBox.Open(stored, _ring, Aad(id));
        if (plain is null)
        {
            return new IntegrationSecrets(null, null);
        }

        try
        {
            return JsonSerializer.Deserialize<IntegrationSecrets>(plain, Json) ?? new IntegrationSecrets(null, null);
        }
        catch (JsonException)
        {
            return new IntegrationSecrets(null, null);
        }
    }
}
