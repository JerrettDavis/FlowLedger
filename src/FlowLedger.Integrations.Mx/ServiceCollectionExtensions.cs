using System.Net.Http.Headers;
using System.Text;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx.CostControl;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Mx;

/// <summary>
/// DI registration for the real MX financial-data provider.
/// </summary>
public static class ServiceCollectionExtensions
{
    private const string MxMediaType = "application/vnd.mx.api.v1+json";

    /// <summary>
    /// Registers the MX provider: a typed <see cref="MxApiClient"/> HttpClient (Basic auth,
    /// MX vendor Accept header, standard resilience handler), the webhook verifier, the manual
    /// refresh cooldown, options, and <see cref="IFinancialDataProvider"/> → <see cref="MxFinancialDataProvider"/>.
    ///
    /// Credentials are read from the <c>Mx</c> section (<see cref="FinancialProviderOptions"/>);
    /// MX-specific tunables from <c>Mx:Provider</c> (<see cref="MxProviderOptions"/>).
    /// </summary>
    public static IServiceCollection AddMxProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // ── Options ──────────────────────────────────────────────────────────────
        // Credentials (Mx:) — AddOptions + fast-fail validator are idempotent if already added
        // by Infrastructure; re-registering is harmless and keeps this extension self-contained.
        services.AddOptions<FinancialProviderOptions>()
            .BindConfiguration(FinancialProviderOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FinancialProviderOptions>, FinancialProviderOptionsValidator>();

        services.Configure<MxProviderOptions>(configuration.GetSection(MxProviderOptions.SectionName));

        // ── Distributed cache for cooldown ─────────────────────────────────────────
        // Uses Redis when wired upstream; otherwise this in-memory fallback satisfies the
        // IDistributedCache contract on a single instance (documented fallback).
        services.AddDistributedMemoryCache();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<MxRefreshCooldown>();

        // ── Webhook verifier (keyed by Mx:WebhookSecret) ───────────────────────────
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<FinancialProviderOptions>>().Value;
            var secret = opts.WebhookSecret
                ?? throw new InvalidOperationException(
                    "Mx:WebhookSecret is required when the MX provider is enabled.");
            return new MxWebhookVerifier(secret);
        });

        // ── Typed HTTP client + resilience ─────────────────────────────────────────
        services.AddHttpClient<MxApiClient>((sp, http) =>
        {
            var opts = sp.GetRequiredService<IOptions<FinancialProviderOptions>>().Value;

            var baseUrl = opts.BaseUrl
                ?? throw new InvalidOperationException(
                    "Mx:BaseUrl is required when the MX provider is enabled.");
            http.BaseAddress = new Uri(baseUrl, UriKind.Absolute);

            var clientId = opts.ClientId
                ?? throw new InvalidOperationException("Mx:ClientId is required when the MX provider is enabled.");
            var apiKey = opts.ApiKey
                ?? throw new InvalidOperationException("Mx:ApiKey is required when the MX provider is enabled.");

            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{apiKey}"));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(MxMediaType));
        })
        .AddStandardResilienceHandler();

        // ── Provider ───────────────────────────────────────────────────────────────
        // Factory registration (not ActivatorUtilities) so the provider can keep an internal
        // constructor that takes internal collaborators (MxApiClient, MxWebhookVerifier).
        services.AddScoped<IFinancialDataProvider>(sp => new MxFinancialDataProvider(
            sp.GetRequiredService<MxApiClient>(),
            sp.GetRequiredService<MxWebhookVerifier>(),
            sp.GetRequiredService<IOptions<MxProviderOptions>>(),
            sp.GetRequiredService<ILogger<MxFinancialDataProvider>>()));

        return services;
    }
}
