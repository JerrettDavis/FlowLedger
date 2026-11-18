using FlowLedger.Application.Features.Categories;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Categories;

public sealed class NaiveBayesCategoryPredictorTests
{
    private static TrainingExample Ex(string merchant, string label, string direction = "Debit", decimal amount = 20m) =>
        new(merchant, "", direction, amount, label);

    private static List<TrainingExample> Corpus()
    {
        var list = new List<TrainingExample>();
        for (var i = 0; i < 20; i++)
        {
            list.Add(Ex($"Joes Taqueria #{100 + i}", "Food/Dining"));
            list.Add(Ex($"Fresh Mart Grocery {i}", "Food/Groceries", amount: 80m));
            list.Add(Ex($"Acme Payroll {i}", "Income/Paycheck", "Credit", 2000m));
        }

        return list;
    }

    [Fact]
    public void Predicts_unseen_store_number_for_known_merchant()
    {
        var model = NaiveBayesCategoryPredictor.Train(Corpus())!;
        var p = model.Predict("Joes Taqueria #999", "", "Debit", 22m);
        p.Should().NotBeNull();
        p!.Label.Should().Be("Food/Dining");
        p.Probability.Should().BeGreaterThan(0.8);
    }

    [Fact]
    public void Blank_text_returns_null_without_throwing()
    {
        var model = NaiveBayesCategoryPredictor.Train(Corpus())!;
        model.Predict("  ", null, "Debit", 5m).Should().BeNull();
    }

    [Fact]
    public void Too_few_labels_is_cold_start()
    {
        NaiveBayesCategoryPredictor.Train(Corpus().Take(10)).Should().BeNull();
    }

    [Fact]
    public void Single_category_cannot_discriminate()
    {
        var one = Enumerable.Range(0, 30).Select(i => Ex($"Shop {i}", "Shopping"));
        NaiveBayesCategoryPredictor.Train(one).Should().BeNull();
    }

    [Fact]
    public void Direction_separates_identical_text()
    {
        var list = new List<TrainingExample>();
        for (var i = 0; i < 12; i++)
        {
            list.Add(Ex("Chase Card", "Fees/Bank Fees", "Debit", 35m));
            list.Add(Ex("Chase Card", "Income/Refunds", "Credit", 35m));
        }

        var model = NaiveBayesCategoryPredictor.Train(list)!;
        model.Predict("Chase Card", "", "Credit", 35m)!.Label.Should().Be("Income/Refunds");
        model.Predict("Chase Card", "", "Debit", 35m)!.Label.Should().Be("Fees/Bank Fees");
    }

    [Fact]
    public void Cross_validation_reports_high_precision_on_separable_data()
    {
        var r = NaiveBayesCategoryPredictor.CrossValidate(Corpus(), folds: 4, threshold: 0.8);
        r.Coverage.Should().BeGreaterThan(0.8);
        r.PrecisionAtThreshold.Should().BeGreaterThan(0.95);
    }
}
