using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.BillSheet;
using FlowLedger.Application.Tests.Fakes;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Application.Tests.BillSheet;

public sealed class BillSheetServiceTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly DateOnly ThisMonth = new(Today.Year, Today.Month, 1);

    private const string Csv =
        " ,Payer,Debt,Perpetual,Optional,Business/Personal,Service,Auto/Manual,Account Number,Due Date,Estimated Cost,Actual Cost,@PREV@,@CUR@\n" +
        "Power Co,JD,No,Yes,No,Personal,Electric,Auto,123-456-7890,4.0,500,480.5,x,\n" +
        "Streamer,JD,No,Yes,Yes,Personal,Streaming,Auto,,15.0,20,19.99,x,x\n" +
        "Cloud,JD,No,Yes,Yes,Business,Software,Auto,,Flux,10,10,,\n" +
        "Total,,,,,,,,,,530,510.49\n" +
        "Total Optional,,,,,,,,,,30,29.99\n" +
        "Monthly Income,,,,,,,,,,,\n" +
        "Sam Expenses,,Debt,Perpetual,Optional,Business/Personal,Service,Auto/Manual,Desciption,Due Date,Estimated Cost,Actual Cost\n" +
        "Loan,Sam,Yes,No,No,Personal,Personal Loan,Auto,12 mo no interest,21st,\"1,000.50\",\"1,000.50\"\n" +
        "Total,,,,,,,,,,1000.5,1000.5\n";

    private static string SampleCsv()
    {
        var prev = ThisMonth.AddMonths(-1).ToString("MMMM", System.Globalization.CultureInfo.InvariantCulture);
        var cur = ThisMonth.ToString("MMMM", System.Globalization.CultureInfo.InvariantCulture);
        return Csv.Replace("@PREV@", prev).Replace("@CUR@", cur);
    }

    private static (BillSheetService Svc, InMemoryStore Store, FakeTransactionRepository Txns) Make()
    {
        var store = new InMemoryStore();
        var txns = new FakeTransactionRepository();
        return (new BillSheetService(store, txns), store, txns);
    }

    [Fact]
    public async Task Import_maps_headers_sections_totals_and_manual_marks()
    {
        var (svc, store, _) = Make();

        var result = await svc.ImportCsvAsync(new ImportSheetRequest(SampleCsv(), false));

        result.Should().Be(new SheetImportResult(2, 4, 3));
        var bills = await store.ListBillsAsync();
        bills.Select(b => b.Section).Distinct().Should().BeEquivalentTo(["Bills", "Sam Expenses"]);
        var power = bills.Single(b => b.Name == "Power Co");
        power.Should().Match<SheetBillData>(b => b.DueDay == 4 && b.Estimated == 500m && b.Actual == 480.5m && b.AutoPay && !b.IsOptional);
        bills.Single(b => b.Name == "Cloud").Should().Match<SheetBillData>(b => b.DueDay == null && b.IsBusiness);
        bills.Single(b => b.Name == "Loan").Should().Match<SheetBillData>(b => b.IsDebt && b.DueDay == 21 && b.Estimated == 1000.5m && b.Payer == "Sam");
        bills.Should().NotContain(b => b.Name.StartsWith("Total") || b.Name.StartsWith("Monthly"));
    }

    [Fact]
    public async Task Import_refuses_to_overwrite_unless_replace_is_set()
    {
        var (svc, _, _) = Make();
        await svc.ImportCsvAsync(new ImportSheetRequest(SampleCsv(), false));

        var again = () => svc.ImportCsvAsync(new ImportSheetRequest(SampleCsv(), false));
        await again.Should().ThrowAsync<SheetOperationException>().WithMessage("*replace*");

        var replaced = await svc.ImportCsvAsync(new ImportSheetRequest(SampleCsv(), true));
        replaced.Bills.Should().Be(4);
    }

    [Fact]
    public async Task View_has_per_section_and_grand_totals_and_masks_account_numbers()
    {
        var (svc, _, _) = Make();
        await svc.ImportCsvAsync(new ImportSheetRequest(SampleCsv(), false));

        var view = await svc.GetViewAsync(BillSheetService.Key(ThisMonth), 3);

        view.Months.Should().HaveCount(3).And.EndWith(BillSheetService.Key(ThisMonth));
        var bills = view.Sections.Single(s => s.Name == "Bills");
        bills.Totals.Estimated.Should().Be(new SheetTotalsLine(530m, 30m, 500m, 520m, 10m));
        view.GrandTotals.Estimated.Total.Should().Be(1530.5m);
        bills.Rows.Single(r => r.Name == "Power Co").AccountRefMasked.Should().Be("••••7890");
        view.Sections.Single(s => s.Name == "Sam Expenses").Rows.Single().AccountRefMasked.Should().Be("12 mo no interest");
    }

    [Fact]
    public async Task Refresh_marks_cells_from_transactions_and_summary_tracks_progress()
    {
        var (svc, _, txns) = Make();
        await svc.ImportCsvAsync(new ImportSheetRequest(SampleCsv(), false));
        var day = new DateOnly(Today.Year, Today.Month, Math.Min(4, DateTime.DaysInMonth(Today.Year, Today.Month)));
        await AddTxn(txns, "AEP POWER CO ONLINE", 455.10m, day);

        var refreshed = await svc.RefreshAsync();
        var view = await svc.GetViewAsync(null, 2);

        refreshed.Marked.Should().BeGreaterThanOrEqualTo(1);
        var cur = BillSheetService.Key(ThisMonth);
        var power = view.Sections.Single(s => s.Name == "Bills").Rows.Single(r => r.Name == "Power Co");
        power.Cells[cur].Should().Match<SheetCellDto>(c => c.Paid && c.Amount == 455.10m && c.Source == "Auto");
        view.Summary.PaidAmount.Should().BeGreaterThanOrEqualTo(455.10m);

        // Idempotent: nothing new on a second run.
        (await svc.RefreshAsync()).Should().Be(new SheetRefreshResult(0, 0));
    }

    [Fact]
    public async Task Manual_unpaid_stops_automation_and_manual_paid_survives_refresh()
    {
        var (svc, store, txns) = Make();
        await svc.ImportCsvAsync(new ImportSheetRequest(SampleCsv(), false));
        var power = (await store.ListBillsAsync()).Single(b => b.Name == "Power Co");
        var cur = BillSheetService.Key(ThisMonth);
        await AddTxn(txns, "POWER CO", 400m, new DateOnly(Today.Year, Today.Month, Math.Min(4, DateTime.DaysInMonth(Today.Year, Today.Month))));

        await svc.SetPaymentAsync(power.Id, cur, new SetPaymentRequest(false, null));
        await svc.RefreshAsync();

        var view = await svc.GetViewAsync(cur, 1);
        view.Sections.SelectMany(s => s.Rows).Single(r => r.Name == "Power Co").Cells[cur].Paid.Should().BeFalse();

        await svc.SetPaymentAsync(power.Id, cur, new SetPaymentRequest(true, 123m));
        await svc.RefreshAsync();
        var after = await svc.GetViewAsync(cur, 1);
        after.Sections.SelectMany(s => s.Rows).Single(r => r.Name == "Power Co").Cells[cur]
            .Should().Match<SheetCellDto>(c => c.Paid && c.Amount == 123m && c.Source == "Manual");
    }

    [Fact]
    public async Task Create_and_update_validate_input_and_keep_masked_account_ref()
    {
        var (svc, store, _) = Make();
        var bad = () => svc.CreateBillAsync(new SaveBillRequest("Bills", " ", null, false, false, false, false, false, null, 1, null, null, null, null));
        await bad.Should().ThrowAsync<SheetOperationException>();

        var id = await svc.CreateBillAsync(new SaveBillRequest("Bills", "Water", "JD", false, true, false, false, true, 14, 120, null, null, "108260316", null));
        var current = (await store.ListBillsAsync()).Single();
        BillSheetService.MaskReference(current.AccountRef).Should().Be("••••0316");

        await svc.UpdateBillAsync(id, new SaveBillRequest("Bills", "Water", "JD", false, true, false, false, true, 14, 125, null, null, "••••0316", "city of tulsa"));

        var updated = (await store.ListBillsAsync()).Single();
        updated.AccountRef.Should().Be("108260316");
        updated.MatchText.Should().Be("city of tulsa");
    }

    [Fact]
    public async Task Importing_another_tab_keeps_existing_sections_and_replace_is_per_section()
    {
        var (svc, store, _) = Make();
        await svc.ImportCsvAsync(new ImportSheetRequest(SampleCsv(), false));
        const string cards = " ,Payer,Perpetual,Optional,Business/Personal,Service,Auto/Manual,Account Number,Due Date,Estimated Cost,Actual Cost\n" +
            "Credit Cards,JD,No,No,,,,,,,\n" +
            "Amazon,JD,No,No,,,,,5th,0,0\n";
        var csv = cards.Replace("Credit Cards,JD,No,No,,,,,,,\n", "");
        csv = csv.Replace(" ,Payer", "Credit Cards,Payer");

        var result = await svc.ImportCsvAsync(new ImportSheetRequest(csv, false));

        result.Bills.Should().Be(1);
        (await store.ListBillsAsync()).Should().HaveCount(5);

        var again = () => svc.ImportCsvAsync(new ImportSheetRequest(csv, false));
        await again.Should().ThrowAsync<SheetOperationException>().WithMessage("*Credit Cards*");
        await svc.ImportCsvAsync(new ImportSheetRequest(csv, true));
        (await store.ListBillsAsync()).Should().HaveCount(5);
    }

    [Fact]
    public async Task Import_tolerates_data_rows_wider_than_the_header()
    {
        var (svc, store, _) = Make();
        const string csv = "Credit Cards,Payer,Perpetual,Optional,Business/Personal,Service,Auto/Manual,Account Number,Due Date,Estimated Cost,Actual Cost\n" +
            "LL Flooring,JD,No,No,,,,,,520,520,x\n";

        var result = await svc.ImportCsvAsync(new ImportSheetRequest(csv, false));

        result.Bills.Should().Be(1);
        (await store.ListBillsAsync()).Single().Estimated.Should().Be(520m);
    }

    [Fact]
    public async Task Clear_returns_a_cell_to_automatic_and_hint_flags_unmatched_rows()
    {
        var (svc, store, _) = Make();
        await svc.ImportCsvAsync(new ImportSheetRequest(SampleCsv(), false));
        var power = (await store.ListBillsAsync()).Single(b => b.Name == "Power Co");
        var cur = BillSheetService.Key(ThisMonth);

        await svc.SetPaymentAsync(power.Id, cur, new SetPaymentRequest(true, 5m));
        await svc.ClearPaymentAsync(power.Id, cur);

        var view = await svc.GetViewAsync(cur, 1);
        var row = view.Sections.SelectMany(x => x.Rows).Single(r => r.Name == "Power Co");
        row.Cells.Should().NotContainKey(cur);
        row.NeedsMatchText.Should().BeTrue();
    }

    private static async Task AddTxn(FakeTransactionRepository repo, string desc, decimal amount, DateOnly date)
    {
        var tx = Transaction.RecordActual(
            TenantId.New(), AccountId.New(), new Money(amount, "USD"), TransactionDirection.Debit, desc, date, date,
            TransactionSource.MxAggregation);
        await repo.AddAsync(tx);
    }

    private sealed class InMemoryStore : IBillSheetStore
    {
        private readonly List<SheetBillData> _bills = [];
        private readonly List<SheetPaymentData> _payments = [];

        public Task<IReadOnlyList<SheetBillData>> ListBillsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SheetBillData>>(_bills.Where(b => b.IsActive).OrderBy(b => b.Section).ThenBy(b => b.SortOrder).ToList());

        public Task<SheetBillData?> GetBillAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_bills.FirstOrDefault(b => b.Id == id));

        public Task SaveBillAsync(SheetBillData bill, CancellationToken ct = default)
        {
            _bills.RemoveAll(b => b.Id == bill.Id);
            _bills.Add(bill);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteBillAsync(Guid id, CancellationToken ct = default)
        {
            _payments.RemoveAll(p => p.BillId == id);
            return Task.FromResult(_bills.RemoveAll(b => b.Id == id) > 0);
        }

        public Task<IReadOnlyList<SheetPaymentData>> ListPaymentsAsync(DateOnly fromMonth, DateOnly toMonth, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SheetPaymentData>>(_payments.Where(p => p.Month >= fromMonth && p.Month <= toMonth).ToList());

        public Task ApplyPaymentsAsync(IReadOnlyList<SheetPaymentData> upserts, IReadOnlyList<(Guid BillId, DateOnly Month)> clears, CancellationToken ct = default)
        {
            foreach (var (b, m) in clears)
            {
                _payments.RemoveAll(p => p.BillId == b && p.Month == m);
            }

            foreach (var u in upserts)
            {
                _payments.RemoveAll(p => p.BillId == u.BillId && p.Month == u.Month);
                _payments.Add(u);
            }

            return Task.CompletedTask;
        }

        public Task AddRangeAsync(IReadOnlyList<SheetBillData> bills, IReadOnlyList<SheetPaymentData> payments, CancellationToken ct = default)
        {
            _bills.AddRange(bills);
            _payments.AddRange(payments);
            return Task.CompletedTask;
        }

        public Task ClearSectionsAsync(IReadOnlyCollection<string> sections, CancellationToken ct = default)
        {
            var ids = _bills.Where(b => sections.Contains(b.Section)).Select(b => b.Id).ToHashSet();
            _bills.RemoveAll(b => ids.Contains(b.Id));
            _payments.RemoveAll(p => ids.Contains(p.BillId));
            return Task.CompletedTask;
        }
    }
}
