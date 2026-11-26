using FlowLedger.Domain.ValueObjects;
namespace FlowLedger.Application.Features.Debt;

public sealed record DebtInput(string Name, decimal Balance, decimal AnnualRatePercent, decimal MinimumPayment);

public sealed record DebtPayoffResult(string Name, int PayoffMonth, decimal InterestPaid);

public sealed record PayoffPlan(
    string Strategy, bool Feasible, int Months, decimal TotalInterest, decimal TotalPaid, IReadOnlyList<DebtPayoffResult> Debts);

/// <summary>
/// Pure month-by-month debt payoff simulation (no I/O). Every debt gets its minimum each month; the extra budget
/// (plus minimums freed by paid-off debts) rolls onto the target debt chosen by the strategy:
/// avalanche = highest APR first (least interest), snowball = smallest balance first (quickest wins).
/// </summary>
public static class DebtPayoffPlanner
{
    public const string Avalanche = "avalanche";
    public const string Snowball = "snowball";
    public const int MaxMonths = 600;

    public static PayoffPlan Plan(IReadOnlyList<DebtInput> debts, decimal extraMonthly, string strategy)
    {
        var live = debts.Where(d => d.Balance > 0).Select(d => new State(d)).ToList();
        var results = new Dictionary<State, DebtPayoffResult>();
        decimal totalInterest = 0, totalPaid = 0;
        var month = 0;

        while (live.Count > 0 && month < MaxMonths)
        {
            month++;
            foreach (var s in live)
            {
                var interest = MoneyRounding.Round(s.Balance * s.Input.AnnualRatePercent / 1200m, 2);
                s.Balance += interest;
                s.Interest += interest;
                totalInterest += interest;
            }

            var pool = extraMonthly + results.Keys.Sum(k => k.Input.MinimumPayment);
            // Minimums first.
            foreach (var s in live)
            {
                var pay = Math.Min(s.Balance, s.Input.MinimumPayment);
                s.Balance -= pay;
                totalPaid += pay;
            }

            // Then roll the extra onto targets in strategy order.
            foreach (var s in Order(live, strategy))
            {
                if (pool <= 0)
                {
                    break;
                }

                var pay = Math.Min(s.Balance, pool);
                s.Balance -= pay;
                pool -= pay;
                totalPaid += pay;
            }

            foreach (var s in live.Where(s => s.Balance <= 0).ToList())
            {
                results[s] = new DebtPayoffResult(s.Input.Name, month, s.Interest);
                live.Remove(s);
            }
        }

        var feasible = live.Count == 0;
        var all = results.Values.ToList();
        all.AddRange(live.Select(s => new DebtPayoffResult(s.Input.Name, -1, s.Interest)));
        return new PayoffPlan(strategy, feasible, month, totalInterest, totalPaid, all);
    }

    private static IEnumerable<State> Order(List<State> live, string strategy) =>
        strategy == Snowball
            ? live.OrderBy(s => s.Balance)
            : live.OrderByDescending(s => s.Input.AnnualRatePercent).ThenBy(s => s.Balance);

    private sealed class State(DebtInput input)
    {
        public DebtInput Input { get; } = input;
        public decimal Balance { get; set; } = input.Balance;
        public decimal Interest { get; set; }
    }
}
