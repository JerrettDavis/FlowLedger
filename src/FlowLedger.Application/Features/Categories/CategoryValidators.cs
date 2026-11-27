using FluentValidation;

namespace FlowLedger.Application.Features.Categories;

internal sealed class CreateCategoryRequestValidator : AbstractValidator<CreateCategoryRequest>
{
    public CreateCategoryRequestValidator()
    {
        RuleFor(x => x.Path)
            .NotEmpty().WithMessage("Path is required.")
            .MaximumLength(500);

        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage("DisplayName is required.")
            .MaximumLength(200);
    }
}

internal sealed class UpdateCategoryRequestValidator : AbstractValidator<UpdateCategoryRequest>
{
    public UpdateCategoryRequestValidator()
    {
        RuleFor(x => x.Path)
            .NotEmpty().WithMessage("Path is required.")
            .MaximumLength(500);

        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage("DisplayName is required.")
            .MaximumLength(200);
    }
}
