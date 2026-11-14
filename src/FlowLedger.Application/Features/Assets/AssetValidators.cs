using FlowLedger.Domain.Aggregates;
using FluentValidation;

namespace FlowLedger.Application.Features.Assets;

internal static class AssetRules
{
    public static readonly string[] Categories = Enum.GetNames<AssetCategory>();
    public static readonly string[] Methods = Enum.GetNames<ValuationMethod>();

    public static bool IsCategory(string? v) => Categories.Contains(v, StringComparer.OrdinalIgnoreCase);
    public static bool IsMethod(string? v) => string.IsNullOrWhiteSpace(v) || Methods.Contains(v, StringComparer.OrdinalIgnoreCase);
}

public sealed class CreateAssetRequestValidator : AbstractValidator<CreateAssetRequest>
{
    public CreateAssetRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Category).Must(AssetRules.IsCategory)
            .WithMessage($"Category must be one of: {string.Join(", ", AssetRules.Categories)}.");
        RuleFor(x => x.CurrentValue).GreaterThanOrEqualTo(0m);
        RuleFor(x => x.Currency).NotEmpty().Length(3).WithMessage("Currency must be a 3-letter ISO 4217 code.");
        RuleFor(x => x.PurchasePrice).GreaterThanOrEqualTo(0m).When(x => x.PurchasePrice.HasValue);
        RuleFor(x => x.Manufacturer).MaximumLength(200);
        RuleFor(x => x.Model).MaximumLength(200);
        RuleFor(x => x.SerialNumber).MaximumLength(200);
        RuleFor(x => x.Notes).MaximumLength(4000);
        RuleFor(x => x.ValuationMethod).Must(AssetRules.IsMethod)
            .WithMessage($"ValuationMethod must be one of: {string.Join(", ", AssetRules.Methods)}.");
        RuleFor(x => x.AnnualRate).InclusiveBetween(0m, 1m).When(x => x.AnnualRate.HasValue)
            .WithMessage("AnnualRate is a fraction between 0 and 1 (0.15 = 15%).");
        RuleFor(x => x.UsefulLifeYears).GreaterThan(0).When(x => x.UsefulLifeYears.HasValue);
        RuleFor(x => x.SalvageValue).GreaterThanOrEqualTo(0m).When(x => x.SalvageValue.HasValue);
        RuleFor(x => x.Attributes).Must(a => a is null || a.Count <= Asset.MaxAttributes)
            .WithMessage($"At most {Asset.MaxAttributes} custom fields are allowed.");
        RuleForEach(x => x.Attributes).Must(kv => kv.Key.Length <= 100 && kv.Value.Length <= 500)
            .WithMessage("Custom field names are limited to 100 characters and values to 500.");
    }
}

public sealed class UpdateAssetRequestValidator : AbstractValidator<UpdateAssetRequest>
{
    public UpdateAssetRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Category).Must(AssetRules.IsCategory)
            .WithMessage($"Category must be one of: {string.Join(", ", AssetRules.Categories)}.");
        RuleFor(x => x.PurchasePrice).GreaterThanOrEqualTo(0m).When(x => x.PurchasePrice.HasValue);
        RuleFor(x => x.Manufacturer).MaximumLength(200);
        RuleFor(x => x.Model).MaximumLength(200);
        RuleFor(x => x.SerialNumber).MaximumLength(200);
        RuleFor(x => x.Notes).MaximumLength(4000);
        RuleFor(x => x.ValuationMethod).Must(AssetRules.IsMethod)
            .WithMessage($"ValuationMethod must be one of: {string.Join(", ", AssetRules.Methods)}.");
        RuleFor(x => x.AnnualRate).InclusiveBetween(0m, 1m).When(x => x.AnnualRate.HasValue);
        RuleFor(x => x.UsefulLifeYears).GreaterThan(0).When(x => x.UsefulLifeYears.HasValue);
        RuleFor(x => x.SalvageValue).GreaterThanOrEqualTo(0m).When(x => x.SalvageValue.HasValue);
        RuleFor(x => x.Attributes).Must(a => a is null || a.Count <= Asset.MaxAttributes)
            .WithMessage($"At most {Asset.MaxAttributes} custom fields are allowed.");
        RuleForEach(x => x.Attributes).Must(kv => kv.Key.Length <= 100 && kv.Value.Length <= 500)
            .WithMessage("Custom field names are limited to 100 characters and values to 500.");
    }
}

public sealed class RecordValuationRequestValidator : AbstractValidator<RecordValuationRequest>
{
    public RecordValuationRequestValidator()
    {
        RuleFor(x => x.Amount).GreaterThanOrEqualTo(0m);
        RuleFor(x => x.Source).MaximumLength(100);
        RuleFor(x => x.Note).MaximumLength(1000);
    }
}
