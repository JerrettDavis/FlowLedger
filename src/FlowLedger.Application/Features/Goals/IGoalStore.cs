using FlowLedger.Application.Abstractions;
using FlowLedger.SharedKernel;
using FluentValidation;

namespace FlowLedger.Application.Features.Goals;

public sealed record GoalRow(Guid Id, string Name, decimal TargetAmount, decimal SavedAmount, DateOnly? TargetDate, uint Version = 0);

/// <summary>Port for per-tenant savings goals.</summary>
public interface IGoalStore
{
    Task<IReadOnlyList<GoalRow>> ListAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Creates or updates a goal and returns its id and new version. With <paramref name="expectedVersion"/> the write only applies if
    /// the row is still at that version, otherwise <c>ConcurrencyConflictException</c>; null keeps last-write-wins.
    /// </summary>
    Task<(Guid Id, uint Version)> UpsertAsync(Guid tenantId, Guid? id, string name, decimal target, decimal saved, DateOnly? targetDate, uint? expectedVersion = null, CancellationToken ct = default);

    Task RemoveAsync(Guid tenantId, Guid id, uint? expectedVersion = null, CancellationToken ct = default);
}

public sealed record SaveGoalRequest(Guid? Id, string Name, decimal TargetAmount, decimal SavedAmount, DateOnly? TargetDate);

internal sealed class SaveGoalRequestValidator : AbstractValidator<SaveGoalRequest>
{
    public SaveGoalRequestValidator()
    {
        RuleFor(x => x.Name).Must(n => !string.IsNullOrWhiteSpace(n) && n.Length <= 120).WithMessage("Give the goal a name of up to 120 characters.");
        RuleFor(x => x.TargetAmount).Must(t => t is > 0m and <= 1_000_000_000m).WithMessage("The target must be greater than 0 and at most 1,000,000,000.");
        RuleFor(x => x.SavedAmount).InclusiveBetween(0m, 1_000_000_000m).WithMessage("The saved amount must be between 0 and 1,000,000,000.");
    }
}

/// <summary>The per-tenant savings-goal quota: a business limit, so it lives here and not in the store (#121).</summary>
public static class GoalLimits
{
    public const int MaxGoals = 200;
}

/// <summary>Creates or updates one goal, refusing a new one past <see cref="GoalLimits.MaxGoals"/>; updating an existing one is always allowed.</summary>
internal sealed class SaveGoalHandler(IGoalStore store, ITenantContext tenant)
{
    public async Task<(Guid Id, uint Version)> HandleAsync(SaveGoalRequest request, uint? expectedVersion = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var existing = await store.ListAsync(tenant.TenantId, ct);
        if (existing.Count >= GoalLimits.MaxGoals && (request.Id is null || existing.All(g => g.Id != request.Id)))
        {
            throw new RequestValidationException("goal", $"You can have at most {GoalLimits.MaxGoals} savings goals. Remove one before adding another.");
        }

        return await store.UpsertAsync(tenant.TenantId, request.Id, request.Name.Trim(), request.TargetAmount, request.SavedAmount, request.TargetDate, expectedVersion, ct);
    }
}
