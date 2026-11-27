using FlowLedger.Application.Features.Categories;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Categories;

public sealed class ProviderCategoryHintsTests
{
    [Theory]
    [InlineData("Gas & Fuel", "Debit", "Transport/Fuel")]
    [InlineData("Food & Dining", "Debit", "Food/Dining")]
    [InlineData("Groceries", "Debit", "Food/Groceries")]
    [InlineData("Mortgage & Rent", "Debit", "Housing/Mortgage")]
    [InlineData("Utilities", "Debit", "Bills/Utilities")]
    [InlineData("Paycheck", "Credit", "Income/Paycheck")]
    [InlineData("Arts & Entertainment", "Debit", "Entertainment")]
    public void Specific_provider_labels_map_to_a_built_in_path(string label, string direction, string expected)
        => ProviderCategoryHints.PathFor(label, direction).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Uncategorized")]
    [InlineData("Shopping")]
    [InlineData("Auto Parts")]
    [InlineData("Other")]
    public void Vague_or_unknown_labels_give_no_hint(string? label)
        => ProviderCategoryHints.PathFor(label, "Debit").Should().BeNull();

    [Fact]
    public void Income_labels_only_apply_to_credits()
        => ProviderCategoryHints.PathFor("Paycheck", "Debit").Should().BeNull();
}
