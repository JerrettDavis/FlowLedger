using FluentValidation;

namespace FlowLedger.Application.Features.RecurringFlows;

public sealed class CreateRecurringFlowRequestValidator : AbstractValidator<CreateRecurringFlowRequest>
{
    private static readonly string[] ValidDirections = Enum.GetNames(typeof(Domain.Aggregates.TransactionDirection));
    private static readonly string[] ValidAmountModels = Enum.GetNames(typeof(Domain.Aggregates.AmountModel));
    private static readonly string[] ValidFrequencies = Enum.GetNames(typeof(Domain.ValueObjects.RecurrenceFrequency));

    public CreateRecurringFlowRequestValidator()
    {
        RuleFor(x => x.AccountId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Amount).GreaterThan(0m).WithMessage("Amount must be positive.");
        RuleFor(x => x.Currency).NotEmpty().Length(3);

        RuleFor(x => x.Direction)
            .NotEmpty()
            .Must(d => ValidDirections.Contains(d, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"Direction must be one of: {string.Join(", ", ValidDirections)}.");

        RuleFor(x => x.AmountModel)
            .NotEmpty()
            .Must(m => ValidAmountModels.Contains(m, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"AmountModel must be one of: {string.Join(", ", ValidAmountModels)}.");

        RuleFor(x => x.RecurrenceFrequency)
            .NotEmpty()
            .Must(f => ValidFrequencies.Contains(f, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"RecurrenceFrequency must be one of: {string.Join(", ", ValidFrequencies)}.");

        RuleFor(x => x.StartDate).NotEmpty();
    }
}

public sealed class UpdateRecurringFlowRequestValidator : AbstractValidator<UpdateRecurringFlowRequest>
{
    private static readonly string[] ValidAmountModels = Enum.GetNames(typeof(Domain.Aggregates.AmountModel));

    public UpdateRecurringFlowRequestValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0m).WithMessage("Amount must be positive.");
        RuleFor(x => x.AmountModel)
            .NotEmpty()
            .Must(m => ValidAmountModels.Contains(m, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"AmountModel must be one of: {string.Join(", ", ValidAmountModels)}.");
    }
}
