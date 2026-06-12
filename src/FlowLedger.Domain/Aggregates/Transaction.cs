using FlowLedger.Domain.Events;
using FlowLedger.Domain.Exceptions;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Domain.Aggregates;

/// <summary>
/// Transaction aggregate root. Represents a single financial event on an account.
///
/// Planned vs actual distinction (PLAN.md §10.5):
/// - Planned transactions have <see cref="TransactionStatus.Planned"/> and no posting date.
/// - Actual transactions arrive via import or manual entry with <see cref="TransactionStatus.Posted"/>.
/// - The <see cref="Match"/> method links an actual to a planned occurrence and records variance.
///
/// Split support: the aggregate owns a list of <see cref="TransactionSplit"/> value items.
/// Their amounts must sum to the parent transaction total.
/// </summary>
public sealed class Transaction : AggregateRoot
{
    private Guid _id;
    private readonly List<TransactionSplit> _splits = [];

    public override Guid Id => _id;
    public TransactionId TransactionId => TransactionId.From(_id);
    public TenantId TenantId { get; }
    public AccountId AccountId { get; }

    public Money Amount { get; private set; }
    public TransactionDirection Direction { get; }
    public string Description { get; private set; }
    public TransactionStatus Status { get; private set; }
    public TransactionSource Source { get; }

    public DateOnly EffectiveDate { get; private set; }
    public DateOnly? PostedDate { get; private set; }

    public CategoryId? CategoryId { get; private set; }
    public string? MerchantName { get; private set; }
    public string? Notes { get; private set; }

    /// <summary>Fingerprint used for deduplication during import.</summary>
    public TransactionFingerprint? Fingerprint { get; private set; }

    /// <summary>
    /// If this transaction was matched to a planned occurrence, this holds the link.
    /// </summary>
    public PlannedOccurrenceId? MatchedOccurrenceId { get; private set; }

    /// <summary>Read-only view of category splits.</summary>
    public IReadOnlyList<TransactionSplit> Splits => _splits.AsReadOnly();

    public DateTimeOffset CreatedAt { get; }

    private Transaction()
    {
        // EF Core constructor — fields initialised by EF.
        Amount = null!;
        Description = null!;
    }

    private Transaction(
        TransactionId id,
        TenantId tenantId,
        AccountId accountId,
        Money amount,
        TransactionDirection direction,
        string description,
        TransactionStatus status,
        TransactionSource source,
        DateOnly effectiveDate,
        DateOnly? postedDate,
        TransactionFingerprint? fingerprint,
        DateTimeOffset createdAt)
    {
        _id = id.Value;
        TenantId = tenantId;
        AccountId = accountId;
        Amount = amount;
        Direction = direction;
        Description = description;
        Status = status;
        Source = source;
        EffectiveDate = effectiveDate;
        PostedDate = postedDate;
        Fingerprint = fingerprint;
        CreatedAt = createdAt;
    }

    // ── Factories ────────────────────────────────────────────────────────────

    /// <summary>Records an actual imported or manually-entered transaction (Posted/Pending).</summary>
    public static Transaction RecordActual(
        TenantId tenantId,
        AccountId accountId,
        Money amount,
        TransactionDirection direction,
        string description,
        DateOnly effectiveDate,
        DateOnly? postedDate,
        TransactionSource source,
        TransactionFingerprint? fingerprint = null)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new EmptyStringException(nameof(description));

        amount.GuardPositive(nameof(amount));

        var status = postedDate.HasValue ? TransactionStatus.Posted : TransactionStatus.Pending;

        var tx = new Transaction(
            TransactionId.New(), tenantId, accountId, amount, direction,
            description.Trim(), status, source, effectiveDate, postedDate,
            fingerprint, DateTimeOffset.UtcNow);

        tx.RaiseEvent(new TransactionImported(tx.TransactionId, tx.TenantId, tx.AccountId, amount, direction, source));
        return tx;
    }

    /// <summary>Creates a planned (forecast-only) transaction row linked to a recurring flow.</summary>
    public static Transaction CreatePlanned(
        TenantId tenantId,
        AccountId accountId,
        Money amount,
        TransactionDirection direction,
        string description,
        DateOnly plannedDate)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new EmptyStringException(nameof(description));

        amount.GuardPositive(nameof(amount));

        return new Transaction(
            TransactionId.New(), tenantId, accountId, amount, direction,
            description.Trim(), TransactionStatus.Planned, TransactionSource.RecurringPlan,
            plannedDate, null, null, DateTimeOffset.UtcNow);
    }

    // ── Behaviour ────────────────────────────────────────────────────────────

    /// <summary>
    /// Matches this actual transaction to a <see cref="PlannedFlowOccurrence"/>.
    /// Transitions status to Matched. Raises <see cref="TransactionMatchedToPlan"/>.
    /// </summary>
    public void Match(PlannedOccurrenceId occurrenceId, ConfidenceScore confidence)
    {
        if (Status is not (TransactionStatus.Posted or TransactionStatus.Pending))
            throw new InvalidStatusTransitionException(
                Status.ToString(), TransactionStatus.Matched.ToString(), nameof(Transaction));

        MatchedOccurrenceId = occurrenceId;
        Status = TransactionStatus.Matched;

        RaiseEvent(new TransactionMatchedToPlan(TransactionId, TenantId, occurrenceId, confidence));
    }

    /// <summary>Marks the transaction as reconciled (user has confirmed it).</summary>
    public void Reconcile()
    {
        if (Status is not (TransactionStatus.Matched or TransactionStatus.Posted))
            throw new InvalidStatusTransitionException(
                Status.ToString(), TransactionStatus.Reconciled.ToString(), nameof(Transaction));

        Status = TransactionStatus.Reconciled;
    }

    /// <summary>Marks a planned transaction as skipped (e.g. a bill was not paid this cycle).</summary>
    public void Skip()
    {
        if (Status != TransactionStatus.Planned)
            throw new InvalidStatusTransitionException(
                Status.ToString(), TransactionStatus.Skipped.ToString(), nameof(Transaction));
        Status = TransactionStatus.Skipped;
    }

    /// <summary>Categorises the transaction. Raises <see cref="TransactionCategorized"/>.</summary>
    public void Categorize(CategoryId categoryId, string? merchantName = null)
    {
        CategoryId = categoryId;
        if (!string.IsNullOrWhiteSpace(merchantName))
            MerchantName = merchantName.Trim();

        RaiseEvent(new TransactionCategorized(TransactionId, TenantId, categoryId));
    }

    /// <summary>
    /// Replaces the split list. All split amounts must share the same currency as the
    /// transaction and sum to the transaction total.
    /// </summary>
    public void SetSplits(IEnumerable<TransactionSplit> splits)
    {
        var list = splits.ToList();

        var total = list.Aggregate(
            Money.Zero(Amount.Currency),
            (sum, s) => sum + s.Amount);

        if (total != Amount)
            throw new SplitAmountMismatchException(total.Amount, Amount.Amount, Amount.Currency.Code);

        _splits.Clear();
        _splits.AddRange(list);
    }

    public void AddNote(string note)
    {
        if (string.IsNullOrWhiteSpace(note))
            throw new EmptyStringException(nameof(note));
        Notes = note.Trim();
    }

    /// <summary>Transitions a Pending transaction to Posted once the bank clears it.</summary>
    public void MarkPosted(DateOnly postedDate)
    {
        if (Status != TransactionStatus.Pending)
            throw new InvalidStatusTransitionException(
                Status.ToString(), TransactionStatus.Posted.ToString(), nameof(Transaction));

        PostedDate = postedDate;
        Status = TransactionStatus.Posted;
    }
}

/// <summary>
/// A single portion of a split transaction, attributed to a specific category.
/// This is a value-like entity owned entirely by its parent Transaction.
/// </summary>
public sealed class TransactionSplit
{
    public Money Amount { get; private set; }
    public CategoryId? CategoryId { get; private set; }
    public string? Notes { get; private set; }

    private TransactionSplit()
    {
        // EF Core parameterless constructor — fields initialised by EF.
        Amount = null!;
    }

    public TransactionSplit(Money amount, CategoryId? categoryId = null, string? notes = null)
    {
        amount.GuardPositive(nameof(amount));
        Amount = amount;
        CategoryId = categoryId;
        Notes = notes?.Trim();
    }
}

public enum TransactionDirection
{
    Debit,
    Credit,
}

public enum TransactionStatus
{
    /// <summary>Forecast-only row. No actual transaction yet.</summary>
    Planned,
    /// <summary>Imported pending transaction, not yet cleared.</summary>
    Pending,
    /// <summary>Cleared and confirmed by the financial institution.</summary>
    Posted,
    /// <summary>Linked to a planned occurrence via the match engine.</summary>
    Matched,
    /// <summary>User has confirmed this transaction is correct.</summary>
    Reconciled,
    /// <summary>A planned row the user has skipped for this cycle.</summary>
    Skipped,
    /// <summary>Ignored (e.g. internal transfer leg already counted elsewhere).</summary>
    Ignored,
    /// <summary>Needs user review (e.g. uncategorised, duplicate suspect).</summary>
    NeedsReview,
}

public enum TransactionSource
{
    Manual,
    CsvImport,
    OfxImport,
    MxAggregation,
    RecurringPlan,
    ApiImport,
}
