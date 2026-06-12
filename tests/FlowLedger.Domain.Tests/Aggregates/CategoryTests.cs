using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.Exceptions;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Domain.Tests.Aggregates;

public sealed class CategoryTests
{
    private static readonly TenantId TenantId = TenantId.New();

    [Fact]
    public void Create_produces_non_system_category_with_unique_id()
    {
        var cat = Category.Create(TenantId, new CategoryPath("Food/Groceries"), "Groceries");
        cat.IsSystem.Should().BeFalse();
        cat.TenantId.Should().Be(TenantId);
        cat.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void Rename_updates_path_and_display_name()
    {
        var cat = Category.Create(TenantId, new CategoryPath("Food"), "Food");
        cat.Rename(new CategoryPath("Food/Dining"), "Dining Out");
        cat.DisplayName.Should().Be("Dining Out");
        cat.Path.Value.Should().Be("Food/Dining");
    }

    [Fact]
    public void Rename_system_category_throws()
    {
        var cat = new Category(
            CategoryId.New(), TenantId, new CategoryPath("System"),
            "System Cat", isSystem: true);

        var act = () => cat.Rename(new CategoryPath("Other"), "Other");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Create_empty_display_name_throws()
    {
        var act = () => Category.Create(TenantId, new CategoryPath("Food"), "  ");
        act.Should().Throw<EmptyStringException>();
    }
}
