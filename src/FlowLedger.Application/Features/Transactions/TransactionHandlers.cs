using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.Transactions;

/// <summary>Lists transactions with optional filtering.</summary>
public sealed class ListTransactionsHandler
{
    private readonly ITransactionRepository _repo;

    public ListTransactionsHandler(ITransactionRepository repo)
        => _repo = repo;

    public async Task<IReadOnlyList<TransactionDto>> HandleAsync(
        ListTransactionsQuery query,
        CancellationToken ct = default)
    {
        AccountId? accountId = query.AccountId.HasValue
            ? AccountId.From(query.AccountId.Value)
            : null;

        var txs = await _repo.ListAsync(accountId, query.From, query.To, query.Skip, query.Take, ct);
        return txs.Select(TransactionMapper.ToDto).ToList().AsReadOnly();
    }
}

/// <summary>Returns a single transaction by ID.</summary>
public sealed class GetTransactionHandler
{
    private readonly ITransactionRepository _repo;

    public GetTransactionHandler(ITransactionRepository repo)
        => _repo = repo;

    public async Task<TransactionDto?> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var tx = await _repo.GetByIdAsync(TransactionId.From(id), ct);
        return tx is null ? null : TransactionMapper.ToDto(tx);
    }
}

/// <summary>Records a manually-entered transaction.</summary>
public sealed class CreateTransactionHandler
{
    private readonly ITransactionRepository _repo;
    private readonly ITenantContext _tenant;

    public CreateTransactionHandler(ITransactionRepository repo, ITenantContext tenant)
    {
        _repo = repo;
        _tenant = tenant;
    }

    public async Task<TransactionDto> HandleAsync(CreateTransactionRequest request, CancellationToken ct = default)
    {
        var direction = Enum.Parse<TransactionDirection>(request.Direction, ignoreCase: true);
        var currency = new Currency(request.Currency.ToUpperInvariant());
        var amount = new Money(request.Amount, currency);

        var tx = Transaction.RecordActual(
            TenantId.From(_tenant.TenantId),
            AccountId.From(request.AccountId),
            amount,
            direction,
            request.Description,
            request.EffectiveDate,
            request.PostedDate,
            TransactionSource.Manual);

        if (request.CategoryId.HasValue)
        {
            tx.Categorize(CategoryId.From(request.CategoryId.Value), request.MerchantName);
        }
        else if (!string.IsNullOrWhiteSpace(request.MerchantName))
        {
            tx.Categorize(CategoryId.New(), request.MerchantName);
        }

        if (!string.IsNullOrWhiteSpace(request.Notes))
        {
            tx.AddNote(request.Notes);
        }

        await _repo.AddAsync(tx, ct);
        await _repo.SaveChangesAsync(ct);

        return TransactionMapper.ToDto(tx);
    }
}

internal static class TransactionMapper
{
    public static TransactionDto ToDto(Transaction t) => new(
        t.Id,
        t.AccountId.Value,
        t.Amount.Amount,
        t.Amount.Currency.Code,
        t.Direction.ToString(),
        t.Description,
        t.Status.ToString(),
        t.Source.ToString(),
        t.EffectiveDate,
        t.PostedDate,
        t.CategoryId?.Value,
        t.MerchantName,
        t.Notes,
        t.Fingerprint?.Value,
        t.CreatedAt);
}
