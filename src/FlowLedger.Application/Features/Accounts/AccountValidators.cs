using FluentValidation;

namespace FlowLedger.Application.Features.Accounts;

public sealed class CreateAccountRequestValidator : AbstractValidator<CreateAccountRequest>
{
    private static readonly string[] ValidAccountTypes = Enum.GetNames(typeof(Domain.Aggregates.AccountType));

    public CreateAccountRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Account name is required.")
            .MaximumLength(200);

        RuleFor(x => x.AccountType)
            .NotEmpty()
            .Must(t => ValidAccountTypes.Contains(t, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"AccountType must be one of: {string.Join(", ", ValidAccountTypes)}.");

        RuleFor(x => x.Currency)
            .NotEmpty()
            .Length(3).WithMessage("Currency must be a 3-letter ISO 4217 code.");

        RuleFor(x => x.StartingBalance)
            .GreaterThanOrEqualTo(0m).WithMessage("Starting balance must be zero or positive for asset accounts.");
    }
}

public sealed class UpdateAccountRequestValidator : AbstractValidator<UpdateAccountRequest>
{
    public UpdateAccountRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Account name is required.")
            .MaximumLength(200);
    }
}
