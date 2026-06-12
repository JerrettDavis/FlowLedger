using FlowLedger.Application.Features.Imports;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features.Imports;

public sealed class CsvParserTests
{
    // ── Basic parsing ─────────────────────────────────────────────────────────

    [Fact]
    public void Parse_Empty_ReturnsEmpty()
    {
        var result = CsvParser.Parse(string.Empty);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_SingleRow_ReturnsOneRow()
    {
        var result = CsvParser.Parse("2024-01-01,-50.00,Grocery Store");
        result.Should().HaveCount(1);
        result[0].Should().Equal("2024-01-01", "-50.00", "Grocery Store");
    }

    [Fact]
    public void Parse_MultipleRows_ReturnsAllRows()
    {
        var csv = "2024-01-01,-50.00,Grocery\r\n2024-01-02,1000.00,Payroll";
        var result = CsvParser.Parse(csv);
        result.Should().HaveCount(2);
        result[1][2].Should().Be("Payroll");
    }

    [Fact]
    public void Parse_UnixLineEndings_ReturnsAllRows()
    {
        var csv = "a,b,c\nd,e,f\ng,h,i";
        var result = CsvParser.Parse(csv);
        result.Should().HaveCount(3);
    }

    // ── Quoted fields ─────────────────────────────────────────────────────────

    [Fact]
    public void Parse_QuotedFieldWithComma_PreservesComma()
    {
        var csv = "2024-01-01,\"-50.00\",\"Grocery, Store\"";
        var result = CsvParser.Parse(csv);
        result.Should().HaveCount(1);
        result[0][2].Should().Be("Grocery, Store");
    }

    [Fact]
    public void Parse_QuotedFieldWithEmbeddedNewline_TreatedAsSingleField()
    {
        var csv = "2024-01-01,-50.00,\"Line1\nLine2\"";
        var result = CsvParser.Parse(csv);
        result.Should().HaveCount(1);
        result[0][2].Should().Be("Line1\nLine2");
    }

    [Fact]
    public void Parse_DoubledQuoteEscape_ProducesSingleQuote()
    {
        var csv = "2024-01-01,-50.00,\"AT&T \"\"Wireless\"\"\"";
        var result = CsvParser.Parse(csv);
        result[0][2].Should().Be("AT&T \"Wireless\"");
    }

    [Fact]
    public void Parse_EmptyQuotedField_ProducesEmptyString()
    {
        var csv = "a,\"\",c";
        var result = CsvParser.Parse(csv);
        result[0][1].Should().Be(string.Empty);
    }

    // ── Custom delimiter ──────────────────────────────────────────────────────

    [Fact]
    public void Parse_TabDelimiter_Works()
    {
        var csv = "date\tamount\tdesc\n2024-01-01\t-50.00\tGrocery";
        var result = CsvParser.Parse(csv, '\t');
        result.Should().HaveCount(2);
        result[1][2].Should().Be("Grocery");
    }

    [Fact]
    public void Parse_SemicolonDelimiter_Works()
    {
        var csv = "date;amount;desc\n2024-01-01;-50.00;Grocery";
        var result = CsvParser.Parse(csv, ';');
        result.Should().HaveCount(2);
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_TrailingNewline_DoesNotProduceExtraRow()
    {
        var csv = "a,b,c\r\nd,e,f\r\n";
        var result = CsvParser.Parse(csv);
        result.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_OnlyHeader_ReturnsSingleRow()
    {
        var result = CsvParser.Parse("Date,Amount,Description");
        result.Should().HaveCount(1);
        result[0].Should().Equal("Date", "Amount", "Description");
    }

    [Fact]
    public void Parse_LeadingAndTrailingWhitespaceInUnquotedField_IsTrimmed()
    {
        var csv = " date , amount , description ";
        var result = CsvParser.Parse(csv);
        result[0].Should().Equal("date", "amount", "description");
    }
}
