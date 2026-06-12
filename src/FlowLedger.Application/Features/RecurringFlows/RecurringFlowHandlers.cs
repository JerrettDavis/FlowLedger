using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.RecurringFlows;

/// <summary>Lists all recurring flows for the current tenant.</summary>
public sealed class ListRecurringFlowsHandler
{
    private readonly IRecurringFlowRepository _repo;

    public ListRecurringFlowsHandler(IRecurringFlowRepository repo)
        => _repo = repo;

    public async Task<IReadOnlyList<RecurringFlowDto>> HandleAsync(CancellationToken ct = default)
    {
        var flows = await _repo.ListAsync(ct);
        return flows.Select(RecurringFlowMapper.ToDto).ToList().AsReadOnly();
    }
}

/// <summary>Returns a single recurring flow by ID.</summary>
public sealed class GetRecurringFlowHandler
{
    private readonly IRecurringFlowRepository _repo;

    public GetRecurringFlowHandler(IRecurringFlowRepository repo)
        => _repo = repo;

    public async Task<RecurringFlowDto?> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var flow = await _repo.GetByIdAsync(RecurringFlowId.From(id), ct);
        return flow is null ? null : RecurringFlowMapper.ToDto(flow);
    }
}

/// <summary>Creates a new recurring flow.</summary>
public sealed class CreateRecurringFlowHandler
{
    private readonly IRecurringFlowRepository _repo;
    private readonly ITenantContext _tenant;

    public CreateRecurringFlowHandler(IRecurringFlowRepository repo, ITenantContext tenant)
    {
        _repo = repo;
        _tenant = tenant;
    }

    public async Task<RecurringFlowDto> HandleAsync(CreateRecurringFlowRequest request, CancellationToken ct = default)
    {
        var direction = Enum.Parse<TransactionDirection>(request.Direction, ignoreCase: true);
        var amountModel = Enum.Parse<AmountModel>(request.AmountModel, ignoreCase: true);
        var currency = new Currency(request.Currency.ToUpperInvariant());
        var amount = new Money(request.Amount, currency);
        var pattern = BuildPattern(request);
        CategoryId? categoryId = request.CategoryId.HasValue
            ? CategoryId.From(request.CategoryId.Value)
            : null;

        var flow = RecurringFlow.Create(
            TenantId.From(_tenant.TenantId),
            AccountId.From(request.AccountId),
            request.Name,
            amount,
            direction,
            pattern,
            request.StartDate,
            request.EndDate,
            amountModel,
            categoryId,
            request.Counterparty);

        await _repo.AddAsync(flow, ct);
        await _repo.SaveChangesAsync(ct);

        return RecurringFlowMapper.ToDto(flow);
    }

    private static RecurrencePattern BuildPattern(CreateRecurringFlowRequest request)
    {
        var freq = Enum.Parse<RecurrenceFrequency>(request.RecurrenceFrequency, ignoreCase: true);
        return freq switch
        {
            RecurrenceFrequency.Daily => RecurrencePattern.Daily(),
            RecurrenceFrequency.Weekly =>
                RecurrencePattern.Weekly(Enum.Parse<DayOfWeek>(request.AnchorDayOfWeek ?? "Monday", ignoreCase: true)),
            RecurrenceFrequency.EveryNWeeks =>
                RecurrencePattern.EveryNWeeks(
                    request.IntervalWeeks ?? 2,
                    Enum.Parse<DayOfWeek>(request.AnchorDayOfWeek ?? "Monday", ignoreCase: true)),
            RecurrenceFrequency.TwiceMonthly =>
                RecurrencePattern.TwiceMonthly(request.DayOfMonth ?? 1, request.SecondDayOfMonth ?? 15),
            RecurrenceFrequency.Monthly =>
                RecurrencePattern.Monthly(request.DayOfMonth ?? 1),
            RecurrenceFrequency.LastBusinessDay =>
                RecurrencePattern.LastBusinessDay(),
            _ => throw new ArgumentOutOfRangeException(nameof(request.RecurrenceFrequency), freq, null)
        };
    }
}

/// <summary>Updates a recurring flow's amount and amount model.</summary>
public sealed class UpdateRecurringFlowHandler
{
    private readonly IRecurringFlowRepository _repo;

    public UpdateRecurringFlowHandler(IRecurringFlowRepository repo)
        => _repo = repo;

    public async Task<RecurringFlowDto?> HandleAsync(Guid id, UpdateRecurringFlowRequest request, CancellationToken ct = default)
    {
        var flow = await _repo.GetByIdAsync(RecurringFlowId.From(id), ct);
        if (flow is null) return null;

        var amountModel = Enum.Parse<AmountModel>(request.AmountModel, ignoreCase: true);
        var newAmount = new Money(request.Amount, flow.Amount.Currency);
        flow.UpdateAmount(newAmount, amountModel);
        await _repo.SaveChangesAsync(ct);

        return RecurringFlowMapper.ToDto(flow);
    }
}

/// <summary>Deactivates a recurring flow.</summary>
public sealed class DeactivateRecurringFlowHandler
{
    private readonly IRecurringFlowRepository _repo;

    public DeactivateRecurringFlowHandler(IRecurringFlowRepository repo)
        => _repo = repo;

    public async Task<bool> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var flow = await _repo.GetByIdAsync(RecurringFlowId.From(id), ct);
        if (flow is null) return false;

        flow.Deactivate();
        await _repo.SaveChangesAsync(ct);
        return true;
    }
}

internal static class RecurringFlowMapper
{
    public static RecurringFlowDto ToDto(RecurringFlow f) => new(
        f.Id,
        f.AccountId.Value,
        f.Name,
        f.Amount.Amount,
        f.Amount.Currency.Code,
        f.Direction.ToString(),
        f.AmountModel.ToString(),
        f.Pattern.Frequency.ToString(),
        f.Pattern.DayOfMonth,
        f.Pattern.SecondDayOfMonth,
        f.Pattern.IntervalWeeks,
        f.Pattern.AnchorDayOfWeek?.ToString(),
        f.ActiveWindow.Start,
        f.ActiveWindow.End,
        f.CategoryId?.Value,
        f.Counterparty,
        f.IsActive,
        f.CreatedAt);
}
