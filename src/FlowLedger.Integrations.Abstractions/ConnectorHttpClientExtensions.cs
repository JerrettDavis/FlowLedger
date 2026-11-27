using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace FlowLedger.Integrations.Abstractions;

/// <summary>Shared HttpClient wiring for connectors (#100): every connector gets the same resilience policy.</summary>
public static class ConnectorHttpClientExtensions
{
    /// <summary>
    /// Registers a typed connector HttpClient with the standard resilience handler. Unsafe methods (POST, PUT, ...) are never
    /// auto-retried: provisioning calls can create billable members or profiles. Returns the builder so a connector can chain
    /// its own handlers (for example a call-ledger guard) behind the resilience pipeline.
    /// </summary>
    public static IHttpClientBuilder AddConnectorHttpClient<TClient>(
        this IServiceCollection services,
        Action<IServiceProvider, HttpClient>? configure = null)
        where TClient : class
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = configure is null
            ? services.AddHttpClient<TClient>()
            : services.AddHttpClient<TClient>(configure);
        builder.AddStandardResilienceHandler(o => o.Retry.DisableForUnsafeHttpMethods());
        return builder;
    }
}
