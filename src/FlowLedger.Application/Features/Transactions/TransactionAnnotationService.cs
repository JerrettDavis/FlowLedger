using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Categories;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.Exceptions;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Features.Transactions;

/// <summary>One portion of a split transaction as supplied by the user.</summary>
public sealed record SplitInput(decimal Amount, Guid? CategoryId, string? Notes);

/// <summary>User annotations on a transaction: a free-text note and category splits (e.g. a $100 Target run = $70 Groceries + $30 Household).</summary>
public sealed class TransactionAnnotationService(ICategorizationStore store, ICategoryRepository categories)
{
    private const int Max = 50_000;
    public const int MaxNoteLength = 1000;

    public async Task SetNoteAsync(Guid transactionId, string? note, CancellationToken ct = default)
    {
        if (note is { Length: > MaxNoteLength })
        {
            throw new ArgumentException($"Notes are limited to {MaxNoteLength} characters.");
        }

        var tx = await FindAsync(transactionId, ct);
        tx.SetNote(note);
        await store.SaveAsync(ct);
    }

    /// <summary>Replaces the splits. An empty list removes them. Splits must be positive and sum to the transaction amount.</summary>
    public async Task SetSplitsAsync(Guid transactionId, IReadOnlyList<SplitInput> splits, CancellationToken ct = default)
    {
        if (splits.Count == 1)
        {
            throw new ArgumentException("A split needs at least two parts.");
        }

        if (splits.Count > 20)
        {
            throw new ArgumentException("A transaction can have at most 20 splits.");
        }

        var tx = await FindAsync(transactionId, ct);
        if (splits.Count == 0)
        {
            tx.ClearSplits();
            await store.SaveAsync(ct);
            return;
        }

        // Category ids come from the request body: they must belong to THIS tenant (the repository is tenant-scoped).
        var known = (await categories.ListAsync(ct)).ToDictionary(c => c.Id, c => c.CategoryId);
        var parts = new List<TransactionSplit>();
        foreach (var s in splits)
        {
            if (s.Amount <= 0)
            {
                throw new ArgumentException("Each split must be a positive amount.");
            }

            CategoryId? categoryId = null;
            if (s.CategoryId is { } id)
            {
                categoryId = known.TryGetValue(id, out var c) ? c : throw new KeyNotFoundException("Category not found.");
            }

            parts.Add(new TransactionSplit(new Money(s.Amount, tx.Amount.Currency), categoryId, s.Notes));
        }

        try
        {
            tx.SetSplits(parts);
        }
        catch (SplitAmountMismatchException)
        {
            throw new ArgumentException($"The splits must add up to the transaction amount ({tx.Amount.Amount:0.00}).");
        }

        await store.SaveAsync(ct);
    }

    private async Task<Transaction> FindAsync(Guid id, CancellationToken ct)
        => (await store.LoadTrackedAsync(Max, ct)).FirstOrDefault(t => t.Id == id)
           ?? throw new KeyNotFoundException("Transaction not found.");
}
