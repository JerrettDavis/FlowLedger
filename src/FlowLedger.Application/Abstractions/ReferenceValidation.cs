using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Abstractions;

/// <summary>An id in a request body does not exist in the caller's tenant (it may belong to another tenant, which looks identical).</summary>
public sealed class ReferencedEntityNotFoundException(string kind, Guid id)
    : Exception($"{kind} {id} was not found.")
{
    public string Kind { get; } = kind;
    public Guid Id { get; } = id;
}

/// <summary>
/// Confirms that account/category ids supplied by a client belong to the current tenant before anything is written (#146).
/// Backed by the tenant-filtered repositories, so a foreign id and a missing id are indistinguishable.
/// </summary>
public interface IReferenceValidator
{
    Task EnsureAccountAsync(Guid accountId, CancellationToken ct = default);

    Task EnsureCategoryAsync(Guid? categoryId, CancellationToken ct = default);
}

internal sealed class ReferenceValidator(IAccountRepository accounts, ICategoryRepository categories) : IReferenceValidator
{
    public async Task EnsureAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        if (await accounts.GetByIdAsync(AccountId.From(accountId), ct) is null)
        {
            throw new ReferencedEntityNotFoundException("Account", accountId);
        }
    }

    public async Task EnsureCategoryAsync(Guid? categoryId, CancellationToken ct = default)
    {
        if (categoryId is { } id && await categories.GetByIdAsync(CategoryId.From(id), ct) is null)
        {
            throw new ReferencedEntityNotFoundException("Category", id);
        }
    }
}
