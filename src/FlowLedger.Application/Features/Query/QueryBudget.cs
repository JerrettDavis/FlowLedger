using System.Diagnostics;

namespace FlowLedger.Application.Features.Query;

/// <summary>
/// Execution budget for one query (#168): an intermediate-row ceiling, a deterministic evaluation-step ceiling and a wall-clock
/// timeout. Exceeding the first two is a "too expensive" 422, the timeout a 408; neither leaves partial results.
/// </summary>
public sealed record QueryBudget(int MaxIntermediateRows, long MaxEvalSteps, TimeSpan Timeout)
{
    public static QueryBudget Default { get; } = new(MaxIntermediateRows: 250_000, MaxEvalSteps: 50_000_000, Timeout: TimeSpan.FromSeconds(10));
}

public enum QueryBudgetKind { Rows, Steps, Timeout }

/// <summary>A query that ran past its <see cref="QueryBudget"/>. The API maps <see cref="QueryBudgetKind.Timeout"/> to 408, the rest to 422.</summary>
public sealed class QueryBudgetExceededException(string message, QueryBudgetKind kind) : Exception(message)
{
    public QueryBudgetKind Kind { get; } = kind;
}

/// <summary>The budget accounting half of the evaluator (#168): step, row and wall-clock checks.</summary>
internal sealed partial class EvalContext
{
    private long _steps;

    public bool Truthy(object? v) => v is true;

    /// <summary>Passes an operator's output through after checking it against the intermediate-row ceiling.</summary>
    public (List<string>, List<object?[]>) Guard((List<string> Names, List<object?[]> Rows) output)
    {
        CheckRows(output.Rows.Count);
        return output;
    }

    private void CheckRows(int count)
    {
        if (count > budget.MaxIntermediateRows)
        {
            throw new QueryBudgetExceededException(
                $"The query produced more than {budget.MaxIntermediateRows:N0} intermediate rows; narrow it with a filter.", QueryBudgetKind.Rows);
        }
    }

    private void Tick()
    {
        var steps = ++_steps;
        if (steps > budget.MaxEvalSteps)
        {
            throw new QueryBudgetExceededException("The query needs too much computation; narrow it with a filter.", QueryBudgetKind.Steps);
        }

        // The clock is read every 256 evaluations (and on the first), so the check itself is cheap.
        if ((steps & 0xFF) == 1)
        {
            ct.ThrowIfCancellationRequested();
            if (clock.Elapsed >= budget.Timeout)
            {
                throw new QueryBudgetExceededException(
                    $"The query ran longer than {budget.Timeout.TotalSeconds:0.#} seconds.", QueryBudgetKind.Timeout);
            }
        }
    }
}
