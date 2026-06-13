using FlowLedger.Application.Features.Imports;
using FlowLedger.Application.Tests.Fakes;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features.Imports;

public sealed class ImportHandlerTests
{
    private static readonly TenantId Tenant = TenantId.From(FakeTenantContext.DefaultTenantId);
    private static readonly Currency Usd = new("USD");

    private static (ImportTransactionsHandler Handler,
                    FakeTransactionRepository TxRepo,
                    FakeAccountRepository AcctRepo,
                    FakePlannedOccurrenceRepository OccRepo,
                    Account Account)
        BuildSut()
    {
        var txRepo = new FakeTransactionRepository();
        var acctRepo = new FakeAccountRepository();
        var occRepo = new FakePlannedOccurrenceRepository();
        var tenant = new FakeTenantContext();
        var matcher = new MatchingEngine(occRepo);

        var account = Account.Create(Tenant, "Checking", AccountType.Checking, new Money(1000m, Usd));
        acctRepo.AddAsync(account).GetAwaiter().GetResult();

        var handler = new ImportTransactionsHandler(txRepo, acctRepo, occRepo, tenant, matcher);
        return (handler, txRepo, acctRepo, occRepo, account);
    }

    private static CsvColumnMapping DefaultMapping() => new(0, 1, 2);

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Import_ValidRows_ImportsAllRows()
    {
        var (handler, txRepo, _, _, account) = BuildSut();
        var csv = "Date,Amount,Description\n2024-01-10,-50.00,Groceries\n2024-01-11,-30.00,Coffee Shop";

        var summary = await handler.HandleAsync(
            new ImportTransactionsCommand(account.Id, csv, DefaultMapping()));

        summary.ImportedCount.Should().Be(2);
        summary.DuplicateCount.Should().Be(0);
        summary.FailedRowCount.Should().Be(0);
        txRepo.All.Should().HaveCount(2);
    }

    [Fact]
    public async Task Import_PositiveAmount_CreatedAsCredit()
    {
        var (handler, txRepo, _, _, account) = BuildSut();
        var csv = "Date,Amount,Description\n2024-01-15,2000.00,Payroll";

        await handler.HandleAsync(new ImportTransactionsCommand(account.Id, csv, DefaultMapping()));

        txRepo.All[0].Direction.Should().Be(TransactionDirection.Credit);
    }

    [Fact]
    public async Task Import_NegativeAmount_CreatedAsDebit()
    {
        var (handler, txRepo, _, _, account) = BuildSut();
        var csv = "Date,Amount,Description\n2024-01-10,-50.00,Grocery";

        await handler.HandleAsync(new ImportTransactionsCommand(account.Id, csv, DefaultMapping()));

        txRepo.All[0].Direction.Should().Be(TransactionDirection.Debit);
        txRepo.All[0].Amount.Amount.Should().Be(50.00m);
    }

    // ── Amount format variations ───────────────────────────────────────────────

    [Theory]
    [InlineData("(100.50)", -100.50)]
    [InlineData("-100.50", -100.50)]
    [InlineData("$100.50", 100.50)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("-1,234.56", -1234.56)]
    [InlineData("(1,234.56)", -1234.56)]
    public async Task Import_VariousAmountFormats_ParsedCorrectly(string rawAmount, decimal expected)
    {
        var (handler, txRepo, _, _, account) = BuildSut();
        var csv = $"Date,Amount,Description\n2024-01-10,{rawAmount},Test";

        var summary = await handler.HandleAsync(
            new ImportTransactionsCommand(account.Id, csv, DefaultMapping()));

        summary.FailedRowCount.Should().Be(0, $"amount '{rawAmount}' should parse");
        txRepo.All[0].Amount.Amount.Should().Be(Math.Abs(expected));
    }

    // ── Date format variations ────────────────────────────────────────────────

    [Theory]
    [InlineData("2024-01-15")]
    [InlineData("1/15/2024")]
    [InlineData("01/15/2024")]
    [InlineData("15-Jan-2024")]
    [InlineData("Jan 15, 2024")]
    public async Task Import_VariousDateFormats_ParsedSuccessfully(string rawDate)
    {
        var (handler, txRepo, _, _, account) = BuildSut();
        var csv = $"Date,Amount,Description\n{rawDate},-50.00,Test";

        var summary = await handler.HandleAsync(
            new ImportTransactionsCommand(account.Id, csv, DefaultMapping()));

        summary.FailedRowCount.Should().Be(0, $"date '{rawDate}' should parse");
        txRepo.All[0].EffectiveDate.Year.Should().Be(2024);
        txRepo.All[0].EffectiveDate.Month.Should().Be(1);
        txRepo.All[0].EffectiveDate.Day.Should().Be(15);
    }

    // ── Duplicate detection ───────────────────────────────────────────────────

    [Fact]
    public async Task Import_SameRowTwiceInFile_OnlyImportsOnce()
    {
        var (handler, txRepo, _, _, account) = BuildSut();
        var csv = "Date,Amount,Description\n2024-01-10,-50.00,Grocery\n2024-01-10,-50.00,Grocery";

        var summary = await handler.HandleAsync(
            new ImportTransactionsCommand(account.Id, csv, DefaultMapping()));

        summary.ImportedCount.Should().Be(1);
        summary.DuplicateCount.Should().Be(1);
        txRepo.All.Should().HaveCount(1);
    }

    [Fact]
    public async Task Import_AlreadyInDb_SkipsAsDbDuplicate()
    {
        var (handler, txRepo, _, _, account) = BuildSut();
        var csv = "Date,Amount,Description\n2024-01-10,-50.00,Grocery";

        // First import
        await handler.HandleAsync(new ImportTransactionsCommand(account.Id, csv, DefaultMapping()));

        // Second import of same CSV
        var summary2 = await handler.HandleAsync(
            new ImportTransactionsCommand(account.Id, csv, DefaultMapping()));

        summary2.ImportedCount.Should().Be(0);
        summary2.DuplicateCount.Should().Be(1);
        txRepo.All.Should().HaveCount(1); // still only one in store
    }

    // ── Malformed row handling ─────────────────────────────────────────────────

    [Fact]
    public async Task Import_BadDateRow_CollectsErrorDoesNotCrash()
    {
        var (handler, txRepo, _, _, account) = BuildSut();
        var csv = "Date,Amount,Description\nNOTADATE,-50.00,Test";

        var summary = await handler.HandleAsync(
            new ImportTransactionsCommand(account.Id, csv, DefaultMapping()));

        summary.FailedRowCount.Should().Be(1);
        summary.RowErrors.Should().HaveCount(1);
        summary.RowErrors[0].RowNumber.Should().Be(2);
        summary.RowErrors[0].Error.Should().Contain("parse date");
        txRepo.All.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_BadAmountRow_CollectsErrorDoesNotCrash()
    {
        var (handler, txRepo, _, _, account) = BuildSut();
        var csv = "Date,Amount,Description\n2024-01-10,NOTANUMBER,Test";

        var summary = await handler.HandleAsync(
            new ImportTransactionsCommand(account.Id, csv, DefaultMapping()));

        summary.FailedRowCount.Should().Be(1);
        txRepo.All.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_MixedGoodAndBadRows_ImportsGoodCollectsBad()
    {
        var (handler, txRepo, _, _, account) = BuildSut();
        var csv = "Date,Amount,Description\n2024-01-10,-50.00,Good\nBADDATE,-50.00,Bad\n2024-01-11,-30.00,AlsoGood";

        var summary = await handler.HandleAsync(
            new ImportTransactionsCommand(account.Id, csv, DefaultMapping()));

        summary.ImportedCount.Should().Be(2);
        summary.FailedRowCount.Should().Be(1);
    }

    [Fact]
    public async Task Import_EmptyDescriptionRow_CollectsError()
    {
        var (handler, txRepo, _, _, account) = BuildSut();
        var csv = "Date,Amount,Description\n2024-01-10,-50.00,";

        var summary = await handler.HandleAsync(
            new ImportTransactionsCommand(account.Id, csv, DefaultMapping()));

        summary.FailedRowCount.Should().Be(1);
        summary.RowErrors[0].Error.Should().Contain("empty");
    }

    // ── Header row handling ───────────────────────────────────────────────────

    [Fact]
    public async Task Import_NoHeaderRow_ParsesAllRows()
    {
        var (handler, txRepo, _, _, account) = BuildSut();
        var csv = "2024-01-10,-50.00,Grocery\n2024-01-11,-30.00,Coffee";
        var mapping = new CsvColumnMapping(0, 1, 2, HasHeaderRow: false);

        var summary = await handler.HandleAsync(
            new ImportTransactionsCommand(account.Id, csv, mapping));

        summary.ImportedCount.Should().Be(2);
    }

    // ── Quoted CSV edge cases ─────────────────────────────────────────────────

    [Fact]
    public async Task Import_QuotedDescriptionWithComma_ParsedCorrectly()
    {
        var (handler, txRepo, _, _, account) = BuildSut();
        var csv = "Date,Amount,Description\n2024-01-10,-50.00,\"Grocery, Store\"";

        var summary = await handler.HandleAsync(
            new ImportTransactionsCommand(account.Id, csv, DefaultMapping()));

        summary.ImportedCount.Should().Be(1);
        txRepo.All[0].Description.Should().Be("Grocery, Store");
    }

    // ── Account not found ─────────────────────────────────────────────────────

    [Fact]
    public async Task Import_UnknownAccount_ThrowsInvalidOperation()
    {
        var (handler, _, _, _, _) = BuildSut();
        var csv = "Date,Amount,Description\n2024-01-10,-50.00,Test";

        var act = () => handler.HandleAsync(
            new ImportTransactionsCommand(Guid.NewGuid(), csv, DefaultMapping()));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Custom delimiter ──────────────────────────────────────────────────────

    [Fact]
    public async Task Import_TabDelimited_Works()
    {
        var (handler, txRepo, _, _, account) = BuildSut();
        var csv = "Date\tAmount\tDescription\n2024-01-10\t-50.00\tGrocery";
        var mapping = new CsvColumnMapping(0, 1, 2, Delimiter: '\t');

        var summary = await handler.HandleAsync(
            new ImportTransactionsCommand(account.Id, csv, mapping));

        summary.ImportedCount.Should().Be(1);
    }

    // ── CSV robustness: quoted fields, clean input, messy unquoted ───────────

    /// <summary>
    /// RFC-4180 quoted field containing an embedded comma.
    /// "Groceries, Inc." must be treated as a single description field, not split into two.
    /// The TryCoalesceField heuristic must not consume the quoted comma as a field delimiter.
    /// </summary>
    [Fact]
    public async Task Import_QuotedFieldWithEmbeddedComma_TreatedAsSingleField()
    {
        var (handler, txRepo, _, _, account) = BuildSut();
        // All three fields are properly quoted; the description contains a literal comma.
        var csv = "Date,Amount,Description\n\"2024-01-10\",\"-50.00\",\"Groceries, Inc.\"";

        var summary = await handler.HandleAsync(
            new ImportTransactionsCommand(account.Id, csv, DefaultMapping()));

        summary.ImportedCount.Should().Be(1, "fully-quoted RFC-4180 row should import without errors");
        summary.FailedRowCount.Should().Be(0);
        txRepo.All[0].Description.Should().Be("Groceries, Inc.",
            "embedded comma inside quotes must not split the description field");
        txRepo.All[0].EffectiveDate.Should().Be(new DateOnly(2024, 1, 10));
        txRepo.All[0].Amount.Amount.Should().Be(50.00m);
    }

    /// <summary>
    /// Standard clean CSV with no special characters — the baseline happy path.
    /// Verifies that date, amount, and description all round-trip correctly for a
    /// typical bank export row.
    /// </summary>
    [Fact]
    public async Task Import_CleanCsv_AllFieldsParsedCorrectly()
    {
        var (handler, txRepo, _, _, account) = BuildSut();
        var csv = "Date,Amount,Description\n2024-06-15,-250.75,Online Transfer";

        var summary = await handler.HandleAsync(
            new ImportTransactionsCommand(account.Id, csv, DefaultMapping()));

        summary.ImportedCount.Should().Be(1, "a clean standard CSV row should import without errors");
        summary.FailedRowCount.Should().Be(0);
        txRepo.All[0].EffectiveDate.Should().Be(new DateOnly(2024, 6, 15));
        txRepo.All[0].Amount.Amount.Should().Be(250.75m);
        txRepo.All[0].Direction.Should().Be(TransactionDirection.Debit);
        txRepo.All[0].Description.Should().Be("Online Transfer");
    }

    /// <summary>
    /// Messy unquoted CSV where the date contains a comma ("Jan 15, 2024") and the
    /// amount uses a thousands separator ("-1,234.56").  Neither field is quoted.
    /// TryCoalesceField must coalesce adjacent fragments to recover both values correctly.
    /// </summary>
    [Fact]
    public async Task Import_MessyUnquotedDateAndAmount_CoalescedCorrectly()
    {
        var (handler, txRepo, _, _, account) = BuildSut();
        // Raw bank export: date and amount both contain commas but are not quoted.
        // The CsvParser splits on every comma, producing raw fragments:
        //   col0="Jan 15"  col1=" 2024"  col2="-1"  col3="234.56"  col4="Coffee Shop"
        // TryCoalesceField must stitch col0+col1 → "Jan 15, 2024" and col2+col3 → "-1,234.56".
        var csv = "Date,Amount,Description\nJan 15, 2024,-1234.56,Coffee Shop";

        var summary = await handler.HandleAsync(
            new ImportTransactionsCommand(account.Id, csv, DefaultMapping()));

        summary.ImportedCount.Should().Be(1, "messy unquoted CSV should coalesce date and amount fragments");
        summary.FailedRowCount.Should().Be(0);
        txRepo.All[0].EffectiveDate.Should().Be(new DateOnly(2024, 1, 15));
        txRepo.All[0].Amount.Amount.Should().Be(1234.56m);
        txRepo.All[0].Direction.Should().Be(TransactionDirection.Debit);
        txRepo.All[0].Description.Should().Be("Coffee Shop");
    }
}
