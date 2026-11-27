using System.Net;
using System.Net.Http.Json;
using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Query;
using FlowLedger.Application.Features.Transactions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>Acceptance tests for the per-query execution budget and the per-tenant /ask concurrency limit (#168).</summary>
[Collection("ApiIntegration")]
public sealed class QueryBudgetEndpointTests(QueryBudgetApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;
    private Guid _tenant;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _tenant = Guid.NewGuid();
        _client = factory.CreateAuthenticatedClient(_tenant);
        var account = (await (await _client.PostAsJsonAsync("/api/accounts",
            new CreateAccountRequest("Checking", "Checking", 0m, "USD", null, null))).Content.ReadFromJsonAsync<AccountDto>())!;
        var today = new DateOnly(2026, 11, 1);   // the queries under test do not depend on the date
        for (var i = 0; i < 3; i++)
        {
            await _client.PostAsJsonAsync("/api/transactions",
                new CreateTransactionRequest(account.Id, 5m + i, "USD", "Debit", "Coffee " + i, today, today, null, "Coffee " + i, null));
        }
    }

    public Task DisposeAsync()
    {
        factory.Budget = new QueryBudget(1, 1_000_000, TimeSpan.FromSeconds(10));
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Run_that_exceeds_the_row_budget_is_422()
    {
        factory.Budget = new QueryBudget(MaxIntermediateRows: 1, MaxEvalSteps: 1_000_000, Timeout: TimeSpan.FromSeconds(10));

        var response = await _client.PostAsJsonAsync("/api/query/run", new RunQueryRequest("transactions | where amount > 0"));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Run_that_exceeds_the_time_budget_is_408()
    {
        factory.Budget = new QueryBudget(MaxIntermediateRows: 1_000_000, MaxEvalSteps: 1_000_000, Timeout: TimeSpan.Zero);

        var response = await _client.PostAsJsonAsync("/api/query/run", new RunQueryRequest("transactions | where amount > 0"));

        response.StatusCode.Should().Be(HttpStatusCode.RequestTimeout);
    }

    [Fact]
    public async Task Run_inside_the_budget_still_succeeds()
    {
        factory.Budget = new QueryBudget(MaxIntermediateRows: 1_000, MaxEvalSteps: 1_000_000, Timeout: TimeSpan.FromSeconds(10));

        var response = await _client.PostAsJsonAsync("/api/query/run", new RunQueryRequest("transactions | summarize n = count()"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ask_beyond_the_per_tenant_concurrency_limit_is_429_and_other_tenants_are_unaffected()
    {
        var gate = factory.Services.GetRequiredService<AskConcurrencyGate>();
        var held = Enumerable.Range(0, AskConcurrencyGate.DefaultMaxPerTenant).Select(_ => gate.TryEnter(_tenant)).ToList();
        try
        {
            held.Should().OnlyContain(l => l != null);

            var blocked = await _client.PostAsJsonAsync("/api/query/ask", new AskQueryRequest("how much did I spend?"));
            blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

            var other = factory.CreateAuthenticatedClient(Guid.NewGuid());
            var ok = await other.PostAsJsonAsync("/api/query/ask", new AskQueryRequest("   "));
            ok.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            held.ForEach(l => l?.Dispose());
        }

        gate.TrackedTenants.Should().Be(0, "released slots leave no per-tenant state behind");
    }
}
