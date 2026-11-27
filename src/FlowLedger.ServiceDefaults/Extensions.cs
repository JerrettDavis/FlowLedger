using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";
    private const string ReadinessEndpointPath = "/ready";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.ConfigureStructuredLogging();

        builder.AddDefaultHealthChecks();

        builder.Services.AddCorrelationIds(); // X-Correlation-Id in/out + log scope (see CorrelationId)

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            // A sync (POST) can legitimately run for minutes and must never be replayed: retrying a write risks a duplicate
            // run, so unsafe methods are not retried and the timeouts allow for long provider calls.
            http.AddStandardResilienceHandler(options =>
            {
                options.Retry.DisableFor(HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete);
                options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(5);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(6);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(10);
            });

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    /// <summary>
    /// <c>Logging:Format=json</c> (env <c>Logging__Format=json</c>) switches the console to one JSON object per line, with scopes
    /// (correlation id, trace id) included, for log shippers. Any other value keeps the default human-readable console.
    /// </summary>
    public static TBuilder ConfigureStructuredLogging<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        if (string.Equals(builder.Configuration["Logging:Format"], "json", StringComparison.OrdinalIgnoreCase))
        {
            builder.Logging.AddJsonConsole(o =>
            {
                o.IncludeScopes = true;
                o.UseUtcTimestamp = true;
                o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
            });
        }

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddMeter("FlowLedger.Api")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("FlowLedger") // custom metrics (FlowLedgerTelemetry in Infrastructure)
                    .AddMeter("Npgsql");    // Npgsql's built-in connection pool / command metrics (no extra package)
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddSource("Npgsql") // Npgsql's built-in command spans (no extra package)
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath, StringComparison.OrdinalIgnoreCase)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath, StringComparison.OrdinalIgnoreCase)
                            && !context.Request.Path.StartsWithSegments(ReadinessEndpointPath, StringComparison.OrdinalIgnoreCase)
                    )
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    builder.Services.AddOpenTelemetry()
        //       .UseAzureMonitor();
        //}

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Health endpoints are mapped in ALL environments and are explicitly anonymous,
        // so they bypass any FallbackPolicy that requires an authenticated user.
        // They are expected to be gated at the network/infrastructure level (internal
        // network only) rather than at the application auth level.
        // See https://aka.ms/aspire/healthchecks for the security implications.

        // All health checks must pass for app to be considered ready to accept traffic after starting.
        app.MapHealthChecks(HealthEndpointPath)
           .AllowAnonymous();

        // Readiness: only checks tagged "ready" (Postgres + migrations, Redis if configured, backup freshness). 503 when
        // a dependency is down; /alive is unaffected so an orchestrator restarts a dead process, not a starved one.
        app.MapHealthChecks(ReadinessEndpointPath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("ready")
        })
        .AllowAnonymous();

        // Only health checks tagged with the "live" tag must pass for app to be considered alive.
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        })
        .AllowAnonymous();

        return app;
    }
}
