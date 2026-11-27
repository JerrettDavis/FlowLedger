using FlowLedger.Application.Features.Query;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlowLedger.Api.Tests;

/// <summary>Host whose per-query execution budget is <see cref="Budget"/> (tiny by default), to prove the 422/408 mapping.</summary>
public sealed class QueryBudgetApiFactory : FlowLedgerApiFactory
{
    public QueryBudget Budget { get; set; } = new(MaxIntermediateRows: 1, MaxEvalSteps: 1_000_000, Timeout: TimeSpan.FromSeconds(10));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<QueryBudget>();
            services.AddScoped(_ => Budget);   // scoped: re-read per request so tests can change it
        });
    }
}
