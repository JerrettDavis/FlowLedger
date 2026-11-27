using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.HomeAssistant;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.Integrations;

/// <summary>Creating, updating and rotating integration definitions.</summary>
public sealed partial class IntegrationService
{
    // ── Definitions ──────────────────────────────────────────────────────────────

    /// <summary>Creates (<paramref name="id"/> null) or updates an integration. A webhook's signing secret is returned once, on creation.</summary>
    public async Task<IntegrationSaveResult> SaveAsync(Guid? id, IntegrationSaveRequest r, CancellationToken ct = default)
    {
        var name = r.Name?.Trim() ?? "";
        if (name.Length is 0 or > 80)
        {
            return IntegrationSaveResult.Fail("name", "Give the integration a name (up to 80 characters).");
        }

        if (!IntegrationUrl.TryValidate(r.Url, AllowLocal, out var url, out var urlError))
        {
            return IntegrationSaveResult.Fail("url", urlError!);
        }

        string? soapEndpoint = null;
        if (r.Kind == IntegrationKind.Soap && !string.IsNullOrWhiteSpace(r.SoapEndpoint))
        {
            if (!IntegrationUrl.TryValidate(r.SoapEndpoint, AllowLocal, out soapEndpoint, out var epError))
            {
                return IntegrationSaveResult.Fail("soapEndpoint", epError!);
            }
        }

        if (!OutboundHeaders.TryClean(r.Headers, out var headers, out var headerError))
        {
            return IntegrationSaveResult.Fail("headers", headerError!);
        }

        var authUser = r.AuthUser?.Trim();
        if (r.Auth == IntegrationAuth.ApiKey)
        {
            authUser = string.IsNullOrEmpty(authUser) ? "X-API-Key" : authUser;
            if (!OutboundHeaders.HeaderName().IsMatch(authUser) || OutboundHeaders.Reserved.Contains(authUser))
            {
                return IntegrationSaveResult.Fail("authUser", "Pick a valid header name for the API key (not Authorization or a reserved header).");
            }
        }
        else if (r.Auth == IntegrationAuth.Basic && string.IsNullOrEmpty(authUser))
        {
            return IntegrationSaveResult.Fail("authUser", "Enter the user name for basic authentication.");
        }
        else if (r.Auth != IntegrationAuth.Basic)
        {
            authUser = null;
        }

        var events = r.Kind == IntegrationKind.Webhook
            ? (r.Events ?? [.. IntegrationEvents.All]).Where(IntegrationEvents.All.Contains).Distinct().ToList()
            : [];
        if (r.Kind == IntegrationKind.Webhook && events.Count == 0)
        {
            return IntegrationSaveResult.Fail("events", "Choose at least one event to send.");
        }

        var existing = id is { } existingId ? await store.GetAsync(TenantId, existingId, ct) : null;
        if (id is not null && existing is null)
        {
            return IntegrationSaveResult.Fail("id", "That integration no longer exists.");
        }

        if (existing is null && (await store.ListAsync(TenantId, ct)).Count >= MaxIntegrationsPerTenant)
        {
            return IntegrationSaveResult.Fail("name", $"You can keep up to {MaxIntegrationsPerTenant} integrations. Remove one first.");
        }

        if (existing is not null && existing.Kind != r.Kind)
        {
            return IntegrationSaveResult.Fail("kind", "The type of an integration can't be changed. Create a new one instead.");
        }

        var credential = string.IsNullOrEmpty(r.Credential) ? null : r.Credential;
        if (r.Auth != IntegrationAuth.None && credential is null && (r.ClearCredential || existing is null || !existing.HasCredential))
        {
            return IntegrationSaveResult.Fail("credential", "Enter the credential (token, password or key) for the chosen authentication.");
        }

        if (credential is { Length: > 4096 })
        {
            return IntegrationSaveResult.Fail("credential", "That credential is too long.");
        }

        string? newSigning = null;
        if (r.Kind == IntegrationKind.Webhook && existing is null)
        {
            newSigning = WebhookSigning.NewSecret();
        }

        if ((credential is not null || newSigning is not null) && !store.CanStoreSecrets)
        {
            return IntegrationSaveResult.Fail(
                "credential",
                "This server has no encryption key, so it will not store credentials or signing secrets. Ask the operator to set Integrations__EncryptionKey (base64, 32 bytes).");
        }

        var clearCredential = r.Auth == IntegrationAuth.None || (r.ClearCredential && credential is null);
        var hasCredential = r.Auth != IntegrationAuth.None && (credential is not null || (!clearCredential && existing?.HasCredential == true));
        var saved = new Integration(
            existing?.Id ?? Guid.NewGuid(), name, r.Kind, r.Enabled,
            new IntegrationConfig(url!, r.Auth, authUser, headers, events, soapEndpoint),
            hasCredential, existing?.HasSigningSecret == true || newSigning is not null,
            existing?.CreatedUtc ?? _clock.GetUtcNow(), existing?.LastUsedUtc, existing?.LastError);
        await store.SaveAsync(TenantId, saved, credential, clearCredential, newSigning, ct);
        return new IntegrationSaveResult(saved, null, newSigning);
    }

    /// <summary>Issues a fresh signing secret for a webhook, shown to the caller exactly once.</summary>
    public async Task<(Integration? Integration, string? Secret, string? Error)> RotateSigningSecretAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await store.GetAsync(TenantId, id, ct);
        if (existing is null || existing.Kind != IntegrationKind.Webhook)
        {
            return (null, null, "Only an existing webhook has a signing secret.");
        }

        if (!store.CanStoreSecrets)
        {
            return (null, null, "This server has no encryption key, so it will not store signing secrets.");
        }

        var secret = WebhookSigning.NewSecret();
        var updated = existing with { HasSigningSecret = true };
        await store.SaveAsync(TenantId, updated, null, false, secret, ct);
        return (updated, secret, null);
    }
}
