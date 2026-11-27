using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx;
using FlowLedger.Integrations.Quiltt;
using FlowLedger.Integrations.SimpleFin;
using FlowLedger.Integrations.Simulated;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlowLedger.Infrastructure;

// Connector registration group (#108): which connectors are on, the registry that serves them, and the local LLM query translator.
public static partial class DependencyInjection
{
    /// <summary>
    /// Provider selection is config-only (Mx:Enabled, SimpleFin:Enabled, Quiltt:Enabled, Connectors:Simulated). When MX is enabled,
    /// <see cref="MxCredentialsOptionsValidator"/> fails fast at startup if a required credential is missing: there is
    /// deliberately NO silent fallback to fake data when a real connector is enabled.
    /// </summary>
    private static void AddConnectors(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MxCredentialsOptions>()
            .BindConfiguration(MxCredentialsOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MxCredentialsOptions>, MxCredentialsOptionsValidator>();

        // Connectors are enabled per instance by configuration; accounts choose their connector (account_connector_links, ADR 0010).
        // Several real connectors may be on together. Simulated is on when no real connector is (or explicitly: Connectors:Simulated=true,
        // which adds fake demo accounts next to real ones - only for trying things out).
        var simpleFinEnabled = FlagOn(configuration, "SimpleFin:Enabled");
        var mxEnabled = FlagOn(configuration, $"{MxCredentialsOptions.SectionName}:Enabled");
        var quilttEnabled = FlagOn(configuration, "Quiltt:Enabled");
        var simulatedEnabled = (!simpleFinEnabled && !mxEnabled && !quilttEnabled) || FlagOn(configuration, "Connectors:Simulated");

        // The first enabled connector (SimpleFIN, MX, Quiltt, Simulated) is also the un-keyed "primary" IFinancialDataProvider for older call sites.
        var connectors = new List<ConnectorDescriptor>();
        if (simpleFinEnabled)
        {
            services.AddSimpleFinProvider(configuration, registerAsDefault: connectors.Count == 0);
            connectors.Add(Describe(ConnectorIds.SimpleFin, "SimpleFIN Bridge", "Aggregator (personal)", SimpleFinFinancialDataProvider.DeclaredTraits));
        }

        if (mxEnabled)
        {
            services.AddMxProvider(configuration, registerAsDefault: connectors.Count == 0);
            connectors.Add(Describe(ConnectorIds.Mx, "MX", "Aggregator (widget)", MxFinancialDataProvider.DeclaredTraits));
        }

        if (quilttEnabled)
        {
            services.AddQuilttProvider(configuration, registerAsDefault: connectors.Count == 0);
            // Per-user profiles: Quiltt isolates each tenant itself, so unlike SimpleFIN/MX it is not tied to the household tenant.
            connectors.Add(Describe(ConnectorIds.Quiltt, "Quiltt", "Aggregator (embedded)", QuilttFinancialDataProvider.DeclaredTraits));
        }

        if (simulatedEnabled)
        {
            services.AddSimulatedProvider(configuration, registerAsDefault: connectors.Count == 0);
            connectors.Add(Describe(ConnectorIds.Simulated, "Simulated demo data", "Demo", SimulatedFinancialDataProvider.DeclaredTraits));
        }

        services.AddSingleton<FlowLedger.Application.Abstractions.IWebhookTelemetry, FlowLedger.Infrastructure.Observability.WebhookTelemetry>();
        services.AddScoped<FlowLedger.Application.Abstractions.IHouseholdTenantAccess, HouseholdTenantAccess>();
        services.AddScoped<FlowLedger.Application.Abstractions.IAccountConnectorLinkStore,
            FlowLedger.Infrastructure.Persistence.Repositories.EfAccountConnectorLinkStore>();
        services.AddScoped<FlowLedger.Application.Abstractions.IProviderAccountCatalog,
            FlowLedger.Infrastructure.Persistence.Repositories.EfProviderAccountCatalog>();
        services.AddScoped<IConnectorRegistry>(sp => new FlowLedger.Infrastructure.Sync.ConnectorRegistry(sp, connectors));
    }

    private static bool FlagOn(IConfiguration configuration, string key) => bool.TryParse(configuration[key], out var on) && on;

    private static ConnectorDescriptor Describe(string id, string displayName, string kind, ConnectorTraits traits) =>
        new(id, displayName, kind, !traits.HasFlag(ConnectorTraits.PerTenantAccess), traits);

    /// <summary>Local LLM (Ollama) for "ask a question" -> query. Loopback by default; data never leaves the machine.</summary>
    private static void AddQueryTranslator(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FlowLedger.Infrastructure.Ai.OllamaOptions>()
            .BindConfiguration(FlowLedger.Infrastructure.Ai.OllamaOptions.SectionName)
            .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _) && o.TimeoutSeconds > 0,
                "Ai:Ollama:BaseUrl must be an absolute URL and TimeoutSeconds positive.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<FlowLedger.Infrastructure.Ai.OllamaOptions>>().Value);
        services.AddHttpClient<FlowLedger.Application.Features.Query.IQueryTranslator, FlowLedger.Infrastructure.Ai.OllamaQueryTranslator>(
            (sp, c) =>
            {
                var o = sp.GetRequiredService<FlowLedger.Infrastructure.Ai.OllamaOptions>();
                c.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
                c.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
            });
    }
}
