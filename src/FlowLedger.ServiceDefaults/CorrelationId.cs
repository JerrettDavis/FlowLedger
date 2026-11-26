using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Request correlation: honours a well-formed inbound <c>X-Correlation-Id</c> (else uses the W3C trace id, else a new
/// GUID), echoes it on every response (errors included), tags the current Activity and puts it in the logger scope so
/// every log line of the request carries it. Registered as an <see cref="IStartupFilter"/> by AddServiceDefaults, so it
/// wraps the whole pipeline without touching each host's Program.cs.
/// </summary>
public static partial class CorrelationId
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "FlowLedger.CorrelationId";

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex Safe();

    /// <summary>Accepts only short, header/log-safe tokens so the value can never smuggle markup or log-forging text.</summary>
    public static bool IsAcceptable(string? value) => value is not null && Safe().IsMatch(value);

    public static string Resolve(string? inbound)
        => IsAcceptable(inbound) ? inbound! : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");

    internal static IServiceCollection AddCorrelationIds(this IServiceCollection services)
    {
        services.AddTransient<IStartupFilter, CorrelationStartupFilter>();
        services.AddHttpContextAccessor();
        services.ConfigureHttpClientDefaults(http => http.AddHttpMessageHandler(sp =>
            new CorrelationForwardingHandler(sp.GetRequiredService<IHttpContextAccessor>())));
        return services;
    }

    private sealed class CorrelationStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nxt) =>
            {
                var id = Resolve(context.Request.Headers[HeaderName].FirstOrDefault());
                context.Items[ItemKey] = id;
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers[HeaderName] = id;
                    return Task.CompletedTask;
                });
                Activity.Current?.SetTag("correlation.id", id);

                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("FlowLedger.Correlation");
                using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
                {
                    await nxt(context);
                }
            });
            next(app);
        };
    }

    /// <summary>Forwards the current request's correlation id on outbound HTTP calls (web to api).</summary>
    private sealed class CorrelationForwardingHandler(IHttpContextAccessor accessor) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Internal hops only (service names like "api" have no dot; plus localhost): never leak the id to third-party
            // providers (MX, SimpleFIN, Home Assistant...) that share the same default HttpClient pipeline.
            if (request.RequestUri is { } uri && !uri.Host.Contains('.')
                && !request.Headers.Contains(HeaderName)
                && accessor.HttpContext?.Items[ItemKey] is string id)
            {
                request.Headers.TryAddWithoutValidation(HeaderName, id);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
