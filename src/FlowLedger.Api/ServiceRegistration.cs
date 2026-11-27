using FlowLedger.Api.Auth;
using FlowLedger.Api.Endpoints;
using FlowLedger.Api.Jobs;
using FlowLedger.Api.Middleware;
using FlowLedger.Api.Tenancy;
using FlowLedger.SharedKernel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Quartz;

namespace FlowLedger.Api;

/// <summary>Service registration groups for the API host, so Program.cs reads as a table of contents (#94).</summary>
internal static class ServiceRegistration
{
    /// <summary>
    /// Tenant context. Dev seam: DevTenantContext resolves X-Tenant-Id with a demo fallback. Non-Development: HeaderTenantContext
    /// fails closed (TenantResolutionException, 401) when no valid X-Tenant-Id header is supplied.
    /// </summary>
    internal static IServiceCollection AddFlowLedgerTenancy(this IServiceCollection services, IWebHostEnvironment environment, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<TenantOverride>();
        // Tenancy:RequireTenantHeader=true makes even Development fail closed (no demo-tenant fallback) - set it on any stack with real users.
        if (environment.IsDevelopment()
            && !configuration.GetValue("Tenancy:RequireTenantHeader", false)
            && string.IsNullOrEmpty(configuration["Api:TenantSigningKey"]))
        {
            services.AddScoped<ITenantContext, DevTenantContext>();
        }
        else
        {
            services.AddScoped<ITenantContext, HeaderTenantContext>();
        }

        return services;
    }

    /// <summary>
    /// The API hosts a durable on-demand job triggered from the verified provider webhook endpoints.
    /// The recurring scheduled sync remains owned by the Worker host.
    /// </summary>
    internal static IServiceCollection AddFlowLedgerQuartz(this IServiceCollection services)
    {
        services.AddQuartz(q => q.AddJob<OnDemandSyncJob>(opts => opts.WithIdentity(OnDemandSyncJob.Key).StoreDurably()));
        services.AddQuartzHostedService(o => o.WaitForJobsToComplete = false);
        return services;
    }

    /// <summary>ProblemDetails: TenantResolutionException is a 401, typed application and provider exceptions go through the central handlers (ADR 0018).</summary>
    internal static IServiceCollection AddFlowLedgerErrorHandling(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            return new WebhookAbuseLimiter(
                cfg.GetValue("RateLimiting:WebhookInvalidPerIpPerMinute", WebhookAbuseLimiter.DefaultInvalidPerIpPerMinute),
                cfg.GetValue("RateLimiting:WebhookVerifiedPerProviderPerMinute", WebhookAbuseLimiter.DefaultVerifiedPerProviderPerMinute));
        });
        services.AddExceptionHandler<FlowLedger.Api.Errors.ConcurrencyExceptionHandler>();
        services.AddExceptionHandler<FlowLedger.Api.Errors.QuotaExceptionHandler>();
        services.AddExceptionHandler<FlowLedger.Api.Errors.ProviderExceptionHandler>();
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = ctx =>
            {
                if (ctx.Exception is TenantResolutionException)
                {
                    ctx.ProblemDetails.Status = StatusCodes.Status401Unauthorized;
                    ctx.ProblemDetails.Title = "Tenant identification required";
                    ctx.HttpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                }
            };
        });
        return services;
    }

    /// <summary>
    /// Custom ApiKey scheme (Authorization: Bearer {key} or X-Api-Key: {key}); see docs/adr/0001-auth-openiddict-deferred.md.
    /// The FallbackPolicy protects everything by default, endpoints opt out via AllowAnonymous(). Api options are validated at startup.
    /// </summary>
    internal static IServiceCollection AddFlowLedgerAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });

        services.AddAuthorization(options =>
        {
            var policy = new AuthorizationPolicyBuilder()
                .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .Build();

            options.DefaultPolicy = policy;
            options.FallbackPolicy = policy;
        });

        services.AddOptions<ApiOptions>()
            .BindConfiguration(ApiOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        return services;
    }

    /// <summary>
    /// Fixed windows partitioned per tenant (authenticated) or per remote IP (anonymous), so one user or one noisy caller cannot
    /// exhaust everyone's budget. See RateLimitPartitions.
    /// </summary>
    internal static IServiceCollection AddFlowLedgerRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddRateLimiter(opts =>
        {
            opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            opts.AddFixedWindowPolicy("api", permitLimit: 300, queueLimit: 10);
            opts.AddFixedWindowPolicy("write", permitLimit: 60, queueLimit: 5);
            // Pre-verification webhook limit per (provider, IP): generous so real deliveries are not starved by junk, no queue.
            opts.AddWebhookPolicy("webhook", configuration.GetValue("RateLimiting:WebhookPerIpPerMinute", RateLimitPartitions.DefaultWebhookPerIpPerMinute));
            // Backstop for every request (per-IP cap + stricter cap on rejected credentials) and Retry-After on 429.
            opts.GlobalLimiter = RateLimitPartitions.CreateGlobalLimiter(configuration);
            opts.OnRejected = RateLimitPartitions.OnRejected;
        });
        return services;
    }
}
