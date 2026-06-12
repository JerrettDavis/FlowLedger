namespace FlowLedger.Domain.ValueObjects;

/// <summary>
/// Immutable half-open date range [Start, End). End is optional (null = open-ended).
/// Used by RecurringFlow to define the active window for occurrence generation.
/// </summary>
public sealed record DateOnlyRange
{
    public DateOnly Start { get; }
    public DateOnly? End { get; }

    public DateOnlyRange(DateOnly start, DateOnly? end = null)
    {
        if (end.HasValue && end.Value < start)
            throw new ArgumentException($"End date {end.Value:O} cannot be before start date {start:O}.", nameof(end));

        Start = start;
        End = end;
    }

    public bool Contains(DateOnly date) =>
        date >= Start && (End is null || date < End.Value);

    public bool IsOpenEnded => End is null;

    public override string ToString() =>
        End.HasValue ? $"[{Start:O}, {End.Value:O})" : $"[{Start:O}, open)";
}
