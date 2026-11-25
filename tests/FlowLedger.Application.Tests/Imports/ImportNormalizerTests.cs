using FlowLedger.Application.Features.Imports;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Imports;

public sealed class ImportNormalizerTests
{
    private static List<List<string>> Rows(NormalizedImport n) => CsvParser.Parse(n.Csv).Skip(1).ToList();

    [Fact]
    public void Mint_uses_transaction_type_for_sign()
    {
        var n = ImportNormalizer.Normalize(
            "\"Date\",\"Description\",\"Original Description\",\"Amount\",\"Transaction Type\",\"Category\",\"Account Name\",\"Labels\",\"Notes\"\n" +
            "\"1/15/2024\",\"Starbucks\",\"STARBUCKS #123\",\"4.50\",\"debit\",\"Coffee\",\"Checking\",\"\",\"\"\n" +
            "\"1/16/2024\",\"Paycheck\",\"ACME PAYROLL\",\"2,000.00\",\"credit\",\"Income\",\"Checking\",\"\",\"\"\n");
        n.FormatId.Should().Be("mint");
        var r = Rows(n);
        r[0].Should().Equal("2024-01-15", "-4.50", "STARBUCKS #123", "Starbucks", "Coffee");
        r[1][1].Should().Be("2000.00");
    }

    [Fact]
    public void Monarch_negative_is_expense()
    {
        var n = ImportNormalizer.Normalize(
            "Date,Merchant,Category,Account,Original Statement,Notes,Amount,Tags\n2024-02-01,Netflix,Entertainment,Visa,NETFLIX.COM,,-15.49,\n");
        n.FormatId.Should().Be("monarch");
        Rows(n)[0].Should().Equal("2024-02-01", "-15.49", "NETFLIX.COM", "Netflix", "Entertainment");
    }

    [Fact]
    public void Ynab_outflow_inflow_with_currency_symbols()
    {
        var n = ImportNormalizer.Normalize(
            "\"Account\",\"Flag\",\"Date\",\"Payee\",\"Category Group/Category\",\"Memo\",\"Outflow\",\"Inflow\"\n" +
            "\"Chk\",\"\",\"03/05/2024\",\"Kroger\",\"Food: Groceries\",\"\",\"$52.10\",\"$0.00\"\n" +
            "\"Chk\",\"\",\"03/06/2024\",\"Employer\",\"Income\",\"\",\"$0.00\",\"$1,500.00\"\n");
        n.FormatId.Should().Be("ynab");
        var r = Rows(n);
        r[0][1].Should().Be("-52.10");
        r[1][1].Should().Be("1500.00");
    }

    [Fact]
    public void Amex_positive_charges_are_inverted()
    {
        var n = ImportNormalizer.Normalize("Date,Receipt,Description,Amount\n01/05/2024,,UBER TRIP,23.40\n01/06/2024,,PAYMENT THANK YOU,-500.00\n");
        n.FormatId.Should().Be("amex");
        var r = Rows(n);
        r[0][1].Should().Be("-23.40");
        r[1][1].Should().Be("500.00");
    }

    [Fact]
    public void Discover_and_apple_card_invert()
    {
        ImportNormalizer.Normalize("Trans. Date,Post Date,Description,Amount,Category\n01/05/2024,01/06/2024,AMAZON,10.00,Shopping\n")
            .Csv.Should().Contain(",-10.00,");
        ImportNormalizer.Normalize("Transaction Date,Clearing Date,Description,Merchant,Category,Type,Amount (USD),Purchaser\n01/05/2024,01/06/2024,APPLE.COM,Apple,Shopping,Purchase,9.99,Me\n")
            .Csv.Should().Contain(",-9.99,");
    }

    [Fact]
    public void Chase_card_keeps_sign()
    {
        var n = ImportNormalizer.Normalize("Transaction Date,Post Date,Description,Category,Type,Amount,Memo\n01/05/2024,01/06/2024,SHELL OIL,Gas,Sale,-40.00,\n");
        n.FormatId.Should().Be("chase");
        Rows(n)[0][1].Should().Be("-40.00");
    }

    [Fact]
    public void CapitalOne_debit_credit_columns()
    {
        var n = ImportNormalizer.Normalize(
            "Transaction Date,Posted Date,Card No.,Description,Category,Debit,Credit\n2024-01-05,2024-01-06,1234,WALMART,Merchandise,12.00,\n2024-01-07,2024-01-08,1234,PAYMENT,Payment,,100.00\n");
        n.FormatId.Should().Be("capital-one");
        var r = Rows(n);
        r[0][1].Should().Be("-12.00");
        r[1][1].Should().Be("100.00");
    }

    [Fact]
    public void WellsFargo_headerless()
    {
        var n = ImportNormalizer.Normalize("\"01/05/2024\",\"-35.00\",\"*\",\"\",\"WHOLEFDS #1\"\n\"01/06/2024\",\"1000.00\",\"*\",\"\",\"DEPOSIT\"\n");
        n.FormatId.Should().Be("wells-fargo");
        n.RowCount.Should().Be(2);
    }

    [Fact]
    public void Semicolon_delimited_generic_with_parentheses_negatives()
    {
        var n = ImportNormalizer.Normalize("Date;Description;Amount\n2024-01-05;Rent;(1200.00)\n");
        Rows(n)[0][1].Should().Be("-1200.00");
    }

    [Fact]
    public void Ofx_sgml_and_entities()
    {
        var ofx = "OFXHEADER:100\nDATA:OFXSGML\n\n<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKTRANLIST>\n" +
                  "<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20240115120000[-5:EST]<TRNAMT>-42.10<FITID>1<NAME>AT&amp;T WIRELESS<MEMO>bill</STMTTRN>\n" +
                  "<STMTTRN><TRNTYPE>CREDIT<DTPOSTED>20240116<TRNAMT>900.00<FITID>2<MEMO>Direct deposit</STMTTRN>\n" +
                  "</BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";
        var n = ImportNormalizer.Normalize(ofx);
        n.FormatId.Should().Be("ofx");
        var r = Rows(n);
        r[0].Should().Equal("2024-01-15", "-42.10", "AT&T WIRELESS", "", "");
        r[1][2].Should().Be("Direct deposit");
    }

    [Fact]
    public void Qif_two_digit_year_and_comma_amounts()
    {
        var n = ImportNormalizer.Normalize("!Type:Bank\nD1/ 5'24\nT-1,234.50\nPLandlord\nLHousing\n^\nD01/06/2024\nT20.00\nPRefund\n^\n");
        n.FormatId.Should().Be("qif");
        var r = Rows(n);
        r[0].Should().Equal("2024-01-05", "-1234.50", "Landlord", "", "Housing");
        r[1][1].Should().Be("20.00");
    }

    [Fact]
    public void Skips_total_rows_and_warns()
    {
        var n = ImportNormalizer.Normalize("Date,Description,Amount\n2024-01-05,Coffee,-3.00\nTotal,,-3.00\n");
        n.RowCount.Should().Be(1);
        n.Warnings.Should().ContainSingle();
    }

    [Theory]
    [InlineData("")]
    [InlineData("foo,bar\n1,2\n")]
    public void Unrecognised_throws(string input)
    {
        var act = () => ImportNormalizer.Normalize(input);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Normalized_output_is_consumable_by_the_csv_parser_with_the_canonical_mapping()
    {
        var n = ImportNormalizer.Normalize("Date,Description,Amount\n2024-01-05,\"Smith, J\",-3.00\n");
        CsvParser.Parse(n.Csv)[1][2].Should().Be("Smith, J");
        NormalizedImport.Mapping.DateColumnIndex.Should().Be(0);
    }
}

public sealed class ImportNormalizerLimitTests
{
    [Fact]
    public void Rejects_oversized_content_before_parsing()
    {
        var act = () => ImportNormalizer.Normalize(new string('x', ImportNormalizer.MaxContentChars + 1));
        act.Should().Throw<InvalidOperationException>().WithMessage("*too large*");
    }

    [Fact]
    public void Rejects_more_rows_than_the_import_limit()
    {
        var sb = new System.Text.StringBuilder("Date,Description,Amount\n");
        for (var i = 0; i < ImportTransactionsHandler.MaxImportRows + 1; i++)
        {
            sb.Append("2024-01-05,Coffee,-3.00\n");
        }

        var act = () => ImportNormalizer.Normalize(sb.ToString());
        act.Should().Throw<InvalidOperationException>().WithMessage("*limit per import*");
    }
}
