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

    /// <summary>Who set <see cref="CategoryId"/>: the user (Manual, never overwritten) or the auto-categorizer (Auto, replaceable).</summary>
    public CategorySource CategorySource { get; private set; } = CategorySource.Manual;

    /// <summary>Model confidence (0..1) when <see cref="CategorySource"/> is Auto and the ML categorizer chose the category; otherwise null.</summary>
    public double? CategoryConfidence { get; private set; }

    /// <summary>A low-confidence ML suggestion awaiting the user's accept. Never changes <see cref="CategoryId"/> by itself.</summary>
    public CategoryId? SuggestedCategoryId { get; private set; }
    public double? SuggestedConfidence { get; private set; }
    public string? MerchantName { get; private set; }
    public string? Notes { get; private set; }

    /// <summary>Cleaned-up merchant name derived from the bank descriptor by the ingestion pipeline. Never replaces <see cref="MerchantName"/> (the user's alias).</summary>
    public string? SuggestedName { get; private set; }

    private string? _tags;

    /// <summary>Free-form labels: a mix of user-added tags and ones from the auto-tagger (see <see cref="ApplyAutoTags"/>).</summary>
    public IReadOnlyList<string> Tags => string.IsNullOrEmpty(_tags) ? [] : _tags.Split(',', StringSplitOptions.RemoveEmptyEntries);

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
        {
            throw new EmptyStringException(nameof(description));
        }

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
        {
            throw new EmptyStringException(nameof(description));
        }

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
        {
            throw new InvalidStatusTransitionException(
                Status.ToString(), TransactionStatus.Matched.ToString(), nameof(Transaction));
        }

        MatchedOccurrenceId = occurrenceId;
        Status = TransactionStatus.Matched;

        RaiseEvent(new TransactionMatchedToPlan(TransactionId, TenantId, occurrenceId, confidence));
    }

    /// <summary>
    /// Settles a pending transaction once the provider reports it as posted. Optionally adopts the
    /// posted record's fingerprint so later syncs dedupe against it. No-op unless currently Pending.
    /// </summary>
    public bool Settle(DateOnly postedDate, TransactionFingerprint? fingerprint = null)
    {
        if (Status != TransactionStatus.Pending)
        {
            return false;
        }

        PostedDate = postedDate;
        Status = TransactionStatus.Posted;
        if (fingerprint is not null)
        {
            Fingerprint = fingerprint;
        }

        return true;
    }

    /// <summary>Marks the transaction as reconciled (user has confirmed it).</summary>
    public void Reconcile()
    {
        if (Status is not (TransactionStatus.Matched or TransactionStatus.Posted))
        {
            throw new InvalidStatusTransitionException(
                Status.ToString(), TransactionStatus.Reconciled.ToString(), nameof(Transaction));
        }

        Status = TransactionStatus.Reconciled;
    }

    /// <summary>Marks a planned transaction as skipped (e.g. a bill was not paid this cycle).</summary>
    public void Skip()
    {
        if (Status != TransactionStatus.Planned)
        {
            throw new InvalidStatusTransitionException(
                Status.ToString(), TransactionStatus.Skipped.ToString(), nameof(Transaction));
        }

        Status = TransactionStatus.Skipped;
    }

    /// <summary>Categorises the transaction. Raises <see cref="TransactionCategorized"/>.</summary>
    public void Categorize(CategoryId categoryId, string? merchantName = null, CategorySource source = CategorySource.Manual, double? confidence = null)
    {
        CategoryId = categoryId;
        CategorySource = source;
        CategoryConfidence = source == CategorySource.Auto ? confidence : null;
        SuggestedCategoryId = null;
        SuggestedConfidence = null;
        if (!string.IsNullOrWhiteSpace(merchantName))
        {
            MerchantName = merchantName.Trim();
        }

        RaiseEvent(new TransactionCategorized(TransactionId, TenantId, categoryId));
    }

    /// <summary>Sets the display name (alias) for this transaction's merchant without touching its category.</summary>
    public bool SetMerchantName(string? merchantName)
    {
        var value = string.IsNullOrWhiteSpace(merchantName) ? null : merchantName.Trim();
        if (string.Equals(MerchantName, value, StringComparison.Ordinal))
        {
            return false;
        }

        MerchantName = value;
        return true;
    }

    /// <summary>Stores the suggested display name. Returns true when it changed.</summary>
    public bool SetSuggestedName(string? name)
    {
        var value = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (string.Equals(SuggestedName, value, StringComparison.Ordinal))
        {
            return false;
        }

        SuggestedName = value;
        return true;
    }

    /// <summary>
    /// Replaces the auto-managed tags: every tag in <paramref name="vocabulary"/> is dropped and <paramref name="auto"/> is added,
    /// so user-added tags outside the vocabulary are never touched. Returns true when the tag set changed.
    /// </summary>
    public bool ApplyAutoTags(IEnumerable<string> auto, IReadOnlySet<string> vocabulary)
    {
        var kept = Tags.Where(t => !vocabulary.Contains(t));
        var next = kept.Concat(auto).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.Ordinal).ToList();
        var joined = next.Count == 0 ? null : string.Join(',', next);
        var current = Tags.OrderBy(t => t, StringComparer.Ordinal);
        if (current.SequenceEqual(next))
        {
            return false;
        }

        _tags = joined;
        return true;
    }

    /// <summary>Removes a category that the auto-categorizer set. A manual category is never cleared by this.</summary>
    public bool ClearAutoCategory()
    {
        if (CategoryId is null || CategorySource != CategorySource.Auto)
        {
            return false;
        }

        CategoryId = null;
        CategoryConfidence = null;
        return true;
    }

    /// <summary>Records (or clears, with null) an ML suggestion without categorizing. Returns true when it changed.</summary>
    public bool SetSuggestion(CategoryId? categoryId, double? confidence)
    {
        if (SuggestedCategoryId == categoryId && SuggestedConfidence == confidence)
        {
            return false;
        }

        SuggestedCategoryId = categoryId;
        SuggestedConfidence = categoryId is null ? null : confidence;
        return true;
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
        {
            throw new SplitAmountMismatchException(total.Amount, Amount.Amount, Amount.Currency.Code);
        }

        _splits.Clear();
        _splits.AddRange(list);
    }

    /// <summary>Removes all splits, returning the transaction to a single category.</summary>
    public void ClearSplits() => _splits.Clear();

    /// <summary>Sets or clears (blank) the note.</summary>
    public void SetNote(string? note) => Notes = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    public void AddNote(string note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            throw new EmptyStringException(nameof(note));
        }

        Notes = note.Trim();
    }

    /// <summary>
    /// Flags this transaction as needing user review (e.g. ambiguous match candidate
    /// or uncertain import row). Can be called on Posted or Pending transactions.
    /// </summary>
    public void FlagNeedsReview()
    {
        if (Status is TransactionStatus.Posted or TransactionStatus.Pending)
        {
            Status = TransactionStatus.NeedsReview;
        }
    }

    /// <summary>
    /// Removes a match link, reverting this actual transaction back to Posted
    /// and releasing the linked planned occurrence for re-matching.
    /// Returns the previously matched occurrence ID (if any).
    /// </summary>
    public PlannedOccurrenceId? Unmatch()
    {
        if (Status is not (TransactionStatus.Matched or TransactionStatus.NeedsReview))
        {
            throw new InvalidStatusTransitionException(
                Status.ToString(), TransactionStatus.Posted.ToString(), nameof(Transaction));
        }

        var previousOccurrenceId = MatchedOccurrenceId;
        MatchedOccurrenceId = null;
        Status = TransactionStatus.Posted;
        return previousOccurrenceId;
    }

    /// <summary>Transitions a Pending transaction to Posted once the bank clears it.</summary>
    public void MarkPosted(DateOnly postedDate)
    {
        if (Status != TransactionStatus.Pending)
        {
            throw new InvalidStatusTransitionException(
                Status.ToString(), TransactionStatus.Posted.ToString(), nameof(Transaction));
        }

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

/// <summary>Provenance of a transaction's category.</summary>
public enum CategorySource
{
    /// <summary>Chosen by the user (or existing data of unknown origin): never overwritten automatically.</summary>
    Manual,
    /// <summary>Filled in by the auto-categorizer: may be replaced when the user corrects a merchant.</summary>
    Auto,
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
