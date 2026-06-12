using FlowLedger.Domain.Exceptions;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Domain.Tests.ValueObjects;

public sealed class CategoryPathTests
{
    [Fact]
    public void Constructs_from_simple_path()
    {
        var path = new CategoryPath("Food");
        path.TopLevel.Should().Be("Food");
        path.IsLeaf.Should().BeTrue();
        path.Parent.Should().BeNull();
    }

    [Fact]
    public void Constructs_from_hierarchical_path()
    {
        var path = new CategoryPath("Food/Groceries");
        path.TopLevel.Should().Be("Food");
        path.IsLeaf.Should().BeFalse();
        path.Parent!.Value.Should().Be("Food");
        path.Segments.Should().HaveCount(2);
    }

    [Fact]
    public void Empty_or_whitespace_throws_EmptyStringException()
    {
        var act = () => new CategoryPath("   ");
        act.Should().Throw<EmptyStringException>();
    }

    [Fact]
    public void Value_equality_holds()
    {
        new CategoryPath("Bills/Utilities").Should().Be(new CategoryPath("Bills/Utilities"));
    }
}
