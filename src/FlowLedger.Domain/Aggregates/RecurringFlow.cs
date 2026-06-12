using FlowLedger.Domain.Events;
using FlowLedger.Domain.Exceptions;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Domain.Aggregates;

/// <summary>
/// RecurringFlow aggregate root. Represents a repeating money event (payroll, rent, subscription).
/// It is the primary input to the forecasting engine; occurrence generation (Milestone 4) uses
/// the <see cref="RecurrencePattern"/> and <see cref="ActiveWindow"/> to produce
/// <see cref="PlannedFlowOccurrence"/> entities.
///
/// Amount models: a fixed amount is stored directly; variable models store only the seed amount;
/// the forecasting engine is responsible for averaging/estimating at runtime.
/// </summary>
public sealed class RecurringFlow : AggregateRoot
{
    private Guid _id;
    private readonly List<PlannedFlowOccurrence> _occurrences = [];

    public override Guid Id => _id;
    public RecurringFlowId RecurringFlowId => RecurringFlowId.From(_id);
    public TenantId TenantId { get; }
    public AccountId AccountId { get; }

    public string Name { get; private set; }
    public Money Amount { get; private set; }
    public AmountModel AmountModel { get; private set; }
    public TransactionDirection Direction { get; }
    public RecurrencePattern Pattern { get; private set; }
    public DateOnlyRange ActiveWindow { get; private set; }
    public CategoryId? CategoryId { get; private set; }
    public string? Counterparty { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Occurrences generated for this flow within the current planning horizon.</summary>
    public IReadOnlyList<PlannedFlowOccurrence> Occurrences => _occurrences.AsReadOnly();

    private RecurringFlow()
    {
        // EF Core constructor — fields initialised by EF.
        Name = null!;
        Amount = null!;
        Pattern = null!;
        ActiveWindow = null!;
    }

    private RecurringFlow(
        RecurringFlowId id,
        TenantId tenantId,
        AccountId accountId,
        string name,
        Money amount,
        AmountModel amountModel,
        TransactionDirection direction,
        RecurrencePattern pattern,
        DateOnlyRange activeWindow,
        CategoryId? categoryId,
        string? counterparty,
        DateTimeOffset createdAt)
    {
        _id = id.Value;
        TenantId = tenantId;
        AccountId = accountId;
        Name = name;
        Amount = amount;
        AmountModel = amountModel;
        Direction = direction;
        Pattern = pattern;
        ActiveWindow = activeWindow;
        CategoryId = categoryId;
        Counterparty = counterparty;
        CreatedAt = createdAt;
    }

    // ── Factory ──────────────────────────────────────────────────────────────

    public static RecurringFlow Create(
        TenantId tenantId,
        AccountId accountId,
        string name,
        Money amount,
        TransactionDirection direction,
        RecurrencePattern pattern,
        DateOnly startDate,
        DateOnly? endDate = null,
        AmountModel amountModel = AmountModel.Fixed,
        CategoryId? categoryId = null,
        string? counterparty = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new EmptyStringException(nameof(name));

        amount.GuardPositive(nameof(amount));

        var flow = new RecurringFlow(
            RecurringFlowId.New(),
            tenantId,
            accountId,
            name.Trim(),
            amount,
            amountModel,
            direction,
            pattern,
            new DateOnlyRange(startDate, endDate),
            categoryId,
            counterparty?.Trim(),
            DateTimeOffset.UtcNow);

        flow.RaiseEvent(new RecurringFlowCreated(flow.RecurringFlowId, flow.TenantId, flow.AccountId, direction));
        return flow;
    }

    // ── Behaviour ────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a <see cref="PlannedFlowOccurrence"/> for the given date.
    /// The date must fall within the active window.
    /// Raises <see cref="PlannedOccurrenceGenerated"/>.
    /// </summary>
    public PlannedFlowOccurrence GenerateOccurrence(DateOnly date, Money? overrideAmount = null)
    {
        if (!ActiveWindow.Contains(date))
            throw new OccurrenceDateOutOfRangeException(date, ActiveWindow.Start, ActiveWindow.End);

        // Create a new Money instance (not a reference copy) so each occurrence owns a distinct
        // value object. EF Core's identity resolution treats owned entities by reference when
        // building the entity graph — sharing the same Money instance across two occurrences
        // would cause EF to nullify one of the PlannedAmount navigations.
        var effectiveAmount = overrideAmount ?? new Money(Amount.Amount, Amount.Currency);
        effectiveAmount.GuardPositive(nameof(effectiveAmount));

        if (effectiveAmount.Currency != Amount.Currency)
            throw new CurrencyMismatchException(Amount.Currency.Code, effectiveAmount.Currency.Code);

        var rfId = RecurringFlowId.From(_id);
        var occurrence = new PlannedFlowOccurrence(
            PlannedOccurrenceId.New(),
            rfId,
            TenantId,
            AccountId,
            effectiveAmount,
            Direction,
            date);

        _occurrences.Add(occurrence);
        RaiseEvent(new PlannedOccurrenceGenerated(occurrence.PlannedOccurrenceId, rfId, TenantId, date, effectiveAmount));
        return occurrence;
    }

    public void UpdateAmount(Money newAmount, AmountModel newModel)
    {
        newAmount.GuardPositive(nameof(newAmount));
        if (newAmount.Currency != Amount.Currency)
            throw new CurrencyMismatchException(Amount.Currency.Code, newAmount.Currency.Code);

        Amount = newAmount;
        AmountModel = newModel;
    }

    public void UpdatePattern(RecurrencePattern newPattern)
    {
        ArgumentNullException.ThrowIfNull(newPattern);
        Pattern = newPattern;
    }

    public void ExtendWindow(DateOnly? newEndDate)
    {
        ActiveWindow = new DateOnlyRange(ActiveWindow.Start, newEndDate);
    }

    public void Deactivate()
    {
        IsActive = false;
    }
}

public enum AmountModel
{
    /// <summary>Fixed amount known in advance.</summary>
    Fixed,
    /// <summary>Estimated — forecasting engine fills in expected value.</summary>
    Estimated,
    /// <summary>Uses the most-recently observed actual amount.</summary>
    LastObserved,
    /// <summary>Average of the last N actuals.</summary>
    AverageOfLast,
    /// <summary>Credit card statement balance (full pay-off model).</summary>
    StatementBalance,
    /// <summary>Minimum payment on a credit/loan account.</summary>
    MinimumPayment,
    /// <summary>Percentage of income (payroll-derived).</summary>
    PercentageOfIncome,
}
