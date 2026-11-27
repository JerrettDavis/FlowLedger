using System.Net.Http.Headers;
using System.Text;
using FlowLedger.Integrations.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.SimpleFin;

public static class ServiceCollectionExtensions
{
    /// <param name="registerAsDefault">Also register as the un-keyed (primary) <see cref="IFinancialDataProvider"/>.</param>
    public static IServiceCollection AddSimpleFinProvider(this IServiceCollection services, IConfiguration configuration, bool registerAsDefault = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SimpleFinOptions>()
            .Bind(configuration.GetSection(SimpleFinOptions.SectionName))
            .Validate(o => string.IsNullOrWhiteSpace(o.AccessUrl) || Uri.TryCreate(o.AccessUrl, UriKind.Absolute, out _),
                "SimpleFin:AccessUrl is not a valid absolute URL.")
            .Validate(o => o.HistoryDays is > 0 and <= 90 && o.OverlapDays >= 0 && o.BackfillDays >= 0,
                "SimpleFin:HistoryDays must be 1-90, and OverlapDays / BackfillDays must not be negative.")
            .ValidateOnStart();

        services.AddConnectorHttpClient<SimpleFinFinancialDataProvider>((sp, http) =>
        {
            var opts = sp.GetRequiredService<IOptions<SimpleFinOptions>>().Value;
            if (string.IsNullOrWhiteSpace(opts.AccessUrl))
            {
                return; // surfaced as NeedsUserAction on first use, so the app still starts and the UI can explain.
            }

            var uri = new Uri(opts.AccessUrl, UriKind.Absolute);
            var userInfo = Uri.UnescapeDataString(uri.UserInfo);
            var clean = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri;
            http.BaseAddress = new Uri(clean.ToString().TrimEnd('/') + "/");
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(userInfo)));
        })
        .AddConnectorCallGuard(
            ConnectorIds.SimpleFin,
            "SimpleFin:CallsPaused",
            sp =>
            {
                var o = sp.GetRequiredService<IOptionsMonitor<SimpleFinOptions>>().CurrentValue;
                return new ConnectorCallPolicy(o.CallsPaused, o.DailyCallBudget, o.MonthlyCallBudget);
            });

        services.AddKeyedScoped<IFinancialDataProvider>("SimpleFIN", (sp, _) => sp.GetRequiredService<SimpleFinFinancialDataProvider>());
        if (registerAsDefault)
        {
            services.AddScoped<IFinancialDataProvider>(sp => sp.GetRequiredService<SimpleFinFinancialDataProvider>());
        }

        return services;
    }
}
