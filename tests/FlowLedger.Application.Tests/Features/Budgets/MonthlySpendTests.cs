using FlowLedger.Application.Features.Budgets;
using FlowLedger.Application.Features.Goals;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features.Budgets;

public sealed class MonthlySpendTests
{
    [Fact]
    public void Query_text_is_stable()
    {
        MonthlySpend.ByCategoryQuery.Should().Be(
            "transactions | where direction == \"Debit\" and is_transfer == false and date >= startofmonth(today()) " +
            "| summarize total = sum(amount) by category");
    }

    [Fact]
    public void Parse_maps_rows_case_insensitively_and_coerces_numbers()
    {
        var result = QueryResultBuilder.A().WithElapsedMs(0).Build();

        var map = MonthlySpend.Parse(result);

        map["food/groceries"].Should().Be(150m);
        map[""].Should().Be(5.5m);
        map["Rent"].Should().Be(0m);
    }

    [Fact]
    public void Parent_budget_rolls_up_children_but_not_prefix_lookalikes()
    {
        var spend = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["Food"] = 10m,
            ["Food/Groceries"] = 100m,
            ["food/dining"] = 30m,
            ["Foodie"] = 999m,
        };

        MonthlySpend.SpentFor("Food", spend).Should().Be(140m);
        MonthlySpend.SpentFor("Food/Groceries", spend).Should().Be(100m);
        MonthlySpend.SpentFor("Travel", spend).Should().Be(0m);
    }

    [Theory]
    [InlineData("Food", 0, true)]
    [InlineData("Food", 100_000_000, true)]
    [InlineData(" ", 10, false)]
    [InlineData("Food", -1, false)]
    [InlineData("Food", 100_000_001, false)]
    public void Budget_validator_keys_each_failure_to_its_property(string category, decimal limit, bool valid)
    {
        var r = new SetBudgetRequestValidator().Validate(SetBudgetRequestBuilder.A().ForCategory(category).Limit(limit).Build());

        r.IsValid.Should().Be(valid);
        if (!valid)
        {
            // Each rule keys to its own property so the API's field errors reach the right input (#211).
            var errors = r.ToDictionary();
            errors.Keys.Should().ContainSingle().Which.Should().Be(string.IsNullOrWhiteSpace(category) ? "Category" : "MonthlyLimit");
        }
    }

    [Theory]
    [InlineData("Car", 100, 0, true)]
    [InlineData("", 100, 0, false)]
    [InlineData("Car", 0, 0, false)]
    [InlineData("Car", 100, -1, false)]
    public void Goal_validator_keys_each_failure_to_its_property(string name, decimal target, decimal saved, bool valid)
    {
        var r = new SaveGoalRequestValidator().Validate(SaveGoalRequestBuilder.A().Named(name).Targeting(target).Saved(saved).Build());

        r.IsValid.Should().Be(valid);
        if (!valid)
        {
            var errors = r.ToDictionary();
            errors.Keys.Should().ContainSingle().Which.Should().Be(name.Length == 0 ? "Name" : target <= 0 ? "TargetAmount" : "SavedAmount");
        }
    }
}
