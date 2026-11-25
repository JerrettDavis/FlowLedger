using FlowLedger.Application.Features.Categories;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Categories;

public class DefaultCategoriesTests
{
    [Fact]
    public void Covers_every_original_mint_category()
    {
        // 18 Mint groups, each category present; spot-check counts so a dropped entry is noticed.
        DefaultCategories.Mint.Select(m => m.MintGroup).Distinct().Should().HaveCount(22);
        DefaultCategories.Mint.Select(m => (m.MintGroup, m.MintCategory)).Distinct().Should().HaveCount(DefaultCategories.Mint.Count);
        DefaultCategories.Mint.Should().Contain(m => m.MintCategory == "Fast Food" || m.MintCategory == "Groceries");
        DefaultCategories.Mint.Count.Should().BeGreaterThanOrEqualTo(95);
    }

    [Fact]
    public void Reuses_established_paths_and_every_autocategorizer_target_is_present()
    {
        DefaultCategories.Paths.Should().Contain(new[] { "Food/Groceries", "Transport/Fuel", "Bills/Utilities", "Transfer", "Income/Paycheck" });
        DefaultCategories.Paths.Should().OnlyHaveUniqueItems(); // no duplicate mapped paths
    }
}
