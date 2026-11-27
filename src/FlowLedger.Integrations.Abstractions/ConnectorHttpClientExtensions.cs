using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Integrations.Abstractions;

/// <summary>Shared HttpClient wiring for connectors (#100): every connector gets the same resilience policy and call guard.</summary>
public static class ConnectorHttpClientExtensions
{
    /// <summary>
    /// Registers a typed connector HttpClient with the standard resilience handler. Unsafe methods (POST, PUT, ...) are never
    /// auto-retried: provisioning calls can create billable members or profiles. Returns the builder so a connector can chain
    /// its own handlers (for example <see cref="AddConnectorCallGuard"/>) behind the resilience pipeline.
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

    /// <summary>
    /// Adds the call-ledger, daily/monthly budget and kill-switch handler (<see cref="ConnectorCallGuardHandler"/>) innermost in the
    /// pipeline, so every real attempt is counted and recorded. The guard is skipped (a transparent pass-through) when no
    /// <see cref="IProviderCallLedger"/> is registered or when <paramref name="bypass"/> says no network call will happen.
    /// </summary>
    /// <param name="builder">The connector client builder, after <see cref="AddConnectorHttpClient{TClient}"/>.</param>
    /// <param name="provider">The ledger key, equal to the connector id (see <see cref="ConnectorIds"/>).</param>
    /// <param name="pauseSetting">The configuration key of the kill switch, named in the refusal message.</param>
    /// <param name="policy">Reads the live limits from the connector's options on every call.</param>
    /// <param name="bypass">Optional: true when calls cost nothing (for example an archive replay).</param>
    public static IHttpClientBuilder AddConnectorCallGuard(
        this IHttpClientBuilder builder,
        string provider,
        string pauseSetting,
        Func<IServiceProvider, ConnectorCallPolicy> policy,
        Func<IServiceProvider, bool>? bypass = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(policy);

        return builder.AddHttpMessageHandler(sp =>
        {
            var ledger = sp.GetService<IProviderCallLedger>();
            if (ledger is null || bypass?.Invoke(sp) == true)
            {
                return new PassThroughHandler();
            }

            return new ConnectorCallGuardHandler(
                ledger,
                provider,
                pauseSetting,
                () => policy(sp),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<ConnectorCallGuardHandler>());
        });
    }

    private sealed class PassThroughHandler : DelegatingHandler
    {
    }
}
