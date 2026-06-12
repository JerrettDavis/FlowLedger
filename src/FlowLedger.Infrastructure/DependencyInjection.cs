using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Events;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Storage;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Simulated;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlowLedger.Infrastructure;

/// <summary>
/// Infrastructure service registration extension. Called from the API and Worker hosts.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers EF Core, PostgreSQL, repositories, provider wiring, and sync service.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">Application configuration (used for connection strings).</param>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── Database ────────────────────────────────────────────────────────────
        // The Aspire Postgres resource is named "flowledger" in AppHost.cs.
        // Aspire injects the connection string under the key "ConnectionStrings:flowledger".
        services.AddDbContext<FlowLedgerDbContext>((serviceProvider, options) =>
        {
            var connectionString = configuration.GetConnectionString("flowledger")
                ?? throw new InvalidOperationException(
                    "Required connection string 'flowledger' was not found. " +
                    "Ensure the Aspire Postgres resource is configured and the " +
                    "connection string is injected via Aspire service defaults.");

            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(FlowLedgerDbContext).Assembly.GetName().Name);
                npgsql.EnableRetryOnFailure(maxRetryCount: 5);
            });

            // Enable sensitive data logging only in Development — never in Production.
#if DEBUG
            options.EnableSensitiveDataLogging(false);
#endif
            options.EnableDetailedErrors(false);
        });

        // ── Domain event dispatching ────────────────────────────────────────────
        // DispatchingDomainEventDispatcher resolves all IDomainEventHandler<TEvent>
        // registrations for each raised event via closed-generic reflection.
        services.AddScoped<IDomainEventDispatcher, DispatchingDomainEventDispatcher>();

        // Auto-register all IDomainEventHandler<> implementations in this assembly.
        var infrastructureAssembly = typeof(DependencyInjection).Assembly;
        foreach (var type in infrastructureAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            foreach (var iface in type.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>)))
            {
                services.AddScoped(iface, type);
            }
        }

        // ── Repositories ────────────────────────────────────────────────────────
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<ITransactionRepository, TransactionRepository>();
        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<IRecurringFlowRepository, RecurringFlowRepository>();
        services.AddScoped<IPlannedOccurrenceRepository, PlannedOccurrenceRepository>();
        services.AddScoped<ISyncCursorStore, EfSyncCursorStore>();

        // ── Provider wiring (TODO(provider-wiring) resolved) ──────────────────
        // Provider selection logic:
        //   1. If Mx:Enabled=true AND Mx:ApiKey is present → real MX provider (extension point, not yet built)
        //   2. Otherwise (default, no key required) → Simulated provider
        //
        // Extension point for real MX provider: replace the else branch below with
        //   services.AddMxProvider(configuration);
        // when FlowLedger.Integrations.Mx is implemented in Milestone 7.
        services.Configure<FinancialProviderOptions>(configuration.GetSection(FinancialProviderOptions.SectionName));
        services.AddSingleton<IValidateOptions<FinancialProviderOptions>, FinancialProviderOptionsValidator>();

        var mxOptions = configuration.GetSection(FinancialProviderOptions.SectionName).Get<FinancialProviderOptions>()
                        ?? new FinancialProviderOptions();

        if (mxOptions.Enabled && !string.IsNullOrWhiteSpace(mxOptions.ApiKey))
        {
            // EXTENSION POINT: Wire real MX provider here.
            // Currently falls through to Simulated because no MX implementation exists yet.
            // TODO(M7-mx): services.AddMxProvider(configuration);
            // Fallback to Simulated until MX is implemented.
            services.AddSimulatedProvider(configuration);
        }
        else
        {
            // Default: Simulated provider — no API key required.
            services.AddSimulatedProvider(configuration);
        }

        // ── Sync service ────────────────────────────────────────────────────────
        services.AddScoped<IFinancialSyncService, FinancialSyncService>();

        // ── Object storage ──────────────────────────────────────────────────────
        services.Configure<ObjectStorageOptions>(configuration.GetSection(ObjectStorageOptions.SectionName));
        services.AddSingleton<IObjectStorage, LocalDiskObjectStorage>();

        return services;
    }
}
