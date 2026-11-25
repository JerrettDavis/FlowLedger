using FlowLedger.Integrations.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Quiltt;

public static class ServiceCollectionExtensions
{
    /// <param name="registerAsDefault">Also register as the un-keyed (primary) <see cref="IFinancialDataProvider"/>.</param>
    public static IServiceCollection AddQuilttProvider(this IServiceCollection services, IConfiguration configuration, bool registerAsDefault = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<QuilttOptions>(configuration.GetSection(QuilttOptions.SectionName));
        services.AddSingleton(TimeProvider.System);

        // The session POST can create a profile, so unsafe methods (all Quiltt calls are POSTs) are never auto-retried.
        services.AddHttpClient<QuilttApiClient>()
            .AddStandardResilienceHandler(o => o.Retry.DisableForUnsafeHttpMethods());

        services.AddScoped(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<QuilttOptions>>();
            QuilttWebhookVerifier? verifier = string.IsNullOrWhiteSpace(opts.Value.WebhookSecret)
                ? null
                : new QuilttWebhookVerifier(opts.Value.WebhookSecret!, opts.Value.WebhookTolerance, sp.GetRequiredService<TimeProvider>());
            return new QuilttFinancialDataProvider(
                sp.GetRequiredService<QuilttApiClient>(), opts, verifier, sp.GetRequiredService<ILogger<QuilttFinancialDataProvider>>());
        });

        services.AddKeyedScoped<IFinancialDataProvider>(QuilttFinancialDataProvider.Name,
            (sp, _) => sp.GetRequiredService<QuilttFinancialDataProvider>());
        if (registerAsDefault)
        {
            services.AddScoped<IFinancialDataProvider>(sp => sp.GetRequiredService<QuilttFinancialDataProvider>());
        }

        return services;
    }
}
