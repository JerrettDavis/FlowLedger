using FluentValidation;

namespace FlowLedger.Application.Features.Transactions;

public sealed class CreateTransactionRequestValidator : AbstractValidator<CreateTransactionRequest>
{
    private static readonly string[] ValidDirections = Enum.GetNames(typeof(Domain.Aggregates.TransactionDirection));

    public CreateTransactionRequestValidator()
    {
        RuleFor(x => x.AccountId)
            .NotEmpty().WithMessage("AccountId is required.");

        RuleFor(x => x.Amount)
            .GreaterThan(0m).WithMessage("Amount must be positive.");

        RuleFor(x => x.Currency)
            .NotEmpty()
            .Length(3).WithMessage("Currency must be a 3-letter ISO 4217 code.");

        RuleFor(x => x.Direction)
            .NotEmpty()
            .Must(d => ValidDirections.Contains(d, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"Direction must be one of: {string.Join(", ", ValidDirections)}.");

        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("Description is required.")
            .MaximumLength(500);

        RuleFor(x => x.EffectiveDate)
            .NotEmpty().WithMessage("EffectiveDate is required.");
    }
}

public sealed class ListTransactionsQueryValidator : AbstractValidator<ListTransactionsQuery>
{
    public ListTransactionsQueryValidator()
    {
        RuleFor(x => x.Take)
            .InclusiveBetween(1, 500).WithMessage("Take must be between 1 and 500.");

        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0).WithMessage("Skip must be zero or positive.");
    }
}
