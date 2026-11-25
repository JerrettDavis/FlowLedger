using FlowLedger.Application.Features.Transactions;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Transactions;

public sealed class TransactionCsvTests
{
    private static TransactionDto Tx(string desc, string? merchant = null, string? notes = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 12.5m, "USD", "Debit", desc, "Posted", "Manual",
            new DateOnly(2026, 11, 3), null, null, merchant, notes, null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Quotes_commas_and_embedded_quotes()
    {
        var csv = TransactionCsv.Build([Tx("Dinner, \"nice\"")]);
        csv.Should().Contain("\"Dinner, \"\"nice\"\"\"");
        csv.Should().Contain("2026-11-03").And.Contain("12.50");
    }

    [Theory]
    [InlineData("=HYPERLINK(\"x\")")]
    [InlineData("+1")]
    [InlineData("-2")]
    [InlineData("@cmd")]
    public void Neutralises_formula_injection(string value)
    {
        TransactionCsv.Text(value).TrimStart('"').Should().StartWith("'");
    }

    [Fact]
    public void Header_and_one_row()
    {
        TransactionCsv.Build([Tx("Coffee")]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(2);
    }
}
