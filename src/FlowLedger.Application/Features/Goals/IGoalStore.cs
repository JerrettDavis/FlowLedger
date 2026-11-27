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

public sealed class SaveGoalRequestValidator : AbstractValidator<SaveGoalRequest>
{
    public SaveGoalRequestValidator()
    {
        RuleFor(x => x)
            .Must(r => !(string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 120 || r.TargetAmount <= 0 || r.TargetAmount > 1_000_000_000m
                || r.SavedAmount < 0 || r.SavedAmount > 1_000_000_000m))
            .WithMessage("A name, a positive target and a non-negative saved amount are required.")
            .OverridePropertyName("goal");
    }
}
