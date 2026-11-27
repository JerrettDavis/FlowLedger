using FlowLedger.Application.Features.Query;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Query;

/// <summary>Deterministic (no wall-clock) tests for the per-query execution budget and the /ask concurrency gate (#168).</summary>
public sealed class QueryBudgetTests
{
    private static readonly DateOnly Today = new(2026, 11, 13);

    private static IReadOnlyDictionary<string, QueryTable> Rows(int n)
    {
        var rows = Enumerable.Range(0, n).Select(i => new object?[] { (decimal)i }).ToList();
        return new Dictionary<string, QueryTable>(StringComparer.OrdinalIgnoreCase) { ["t"] = new(["n"], rows) };
    }

    [Fact]
    public void Intermediate_row_ceiling_is_a_rows_error()
    {
        var act = () => QueryEngine.Execute("t | where n >= 0", Rows(100), Today, new QueryBudget(50, 1_000_000, TimeSpan.FromMinutes(1)));

        act.Should().Throw<QueryBudgetExceededException>().Which.Kind.Should().Be(QueryBudgetKind.Rows);
    }

    [Fact]
    public void Evaluation_step_ceiling_is_a_steps_error()
    {
        var act = () => QueryEngine.Execute("t | where n >= 0", Rows(100), Today, new QueryBudget(1_000_000, 10, TimeSpan.FromMinutes(1)));

        act.Should().Throw<QueryBudgetExceededException>().Which.Kind.Should().Be(QueryBudgetKind.Steps);
    }

    [Fact]
    public void Elapsed_timeout_is_a_timeout_error()
    {
        var act = () => QueryEngine.Execute("t | where n >= 0", Rows(100), Today, new QueryBudget(1_000_000, 1_000_000, TimeSpan.Zero));

        act.Should().Throw<QueryBudgetExceededException>().Which.Kind.Should().Be(QueryBudgetKind.Timeout);
    }

    [Fact]
    public void Cancellation_stops_a_running_query()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => QueryEngine.Execute("t | where n >= 0", Rows(100), Today, null, cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Default_budget_lets_normal_queries_run()
    {
        QueryEngine.Execute("t | summarize total = sum(n)", Rows(1000), Today).Rows.Should().HaveCount(1);
    }

    [Fact]
    public void Gate_allows_the_limit_per_tenant_independently_and_forgets_tenants_once_released()
    {
        var gate = new AskConcurrencyGate(maxPerTenant: 2);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var first = gate.TryEnter(a);
        var second = gate.TryEnter(a);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        gate.TryEnter(a).Should().BeNull("a third concurrent ask for the same tenant is refused");
        gate.TryEnter(b).Should().NotBeNull("another tenant has its own slots");

        first!.Dispose();
        first.Dispose();   // double dispose must not free a second slot
        gate.TryEnter(a).Should().NotBeNull();
        gate.TryEnter(a).Should().BeNull();
    }

    [Fact]
    public void Gate_state_is_bounded_by_in_flight_requests_not_tenants_seen()
    {
        var gate = new AskConcurrencyGate();
        for (var i = 0; i < 5000; i++)
        {
            gate.TryEnter(Guid.NewGuid())!.Dispose();
        }

        gate.TrackedTenants.Should().Be(0);
    }
}
