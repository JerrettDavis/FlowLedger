using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.BillSheet;
using FlowLedger.Application.Tests.Fakes;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Application.Tests.BillSheet;

public sealed class BillSheetServiceTests
{
    private static readonly DateOnly Today = FlowLedger.TestSupport.TestClock.Today;
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
        return (new BillSheetService(store, txns, FlowLedger.TestSupport.TestClock.Provider), store, txns);
    }

    [Fact]
    public async Task Refresh_does_not_build_rows_from_investment_account_activity()
    {
        var store = new InMemoryStore();
        var txns = new FakeTransactionRepository();
        var accounts = new FakeAccountRepository();
        var tenant = TenantId.New();
        var brokerage = Account.Create(tenant, "Brokerage", AccountType.Investment, new Money(1000m, "USD"), FlowLedger.TestSupport.TestClock.Provider);
        var checking = Account.Create(tenant, "Checking", AccountType.Checking, new Money(1000m, "USD"), FlowLedger.TestSupport.TestClock.Provider);
        await accounts.AddAsync(brokerage);
        await accounts.AddAsync(checking);
        var svc = new BillSheetService(store, txns, FlowLedger.TestSupport.TestClock.Provider, accounts);

        for (var i = 0; i < 4; i++)
        {
            var d = ThisMonth.AddMonths(-i).AddDays(9);
            await txns.AddAsync(Transaction.RecordActual(tenant, brokerage.AccountId, new Money(500m, "USD"),
                TransactionDirection.Debit, "ACME BROKERAGE CONTRIBUTION", d, d, TransactionSource.MxAggregation, FlowLedger.TestSupport.TestClock.Provider));
            await txns.AddAsync(Transaction.RecordActual(tenant, checking.AccountId, new Money(39.99m, "USD"),
                TransactionDirection.Debit, "Peak Fitness Club", d, d, TransactionSource.MxAggregation, FlowLedger.TestSupport.TestClock.Provider));
        }

        await svc.RefreshAsync();

        var names = (await store.ListBillsAsync(includeInactive: true)).Select(b => b.Name).ToList();
        names.Should().Contain(n => n.Contains("Peak Fitness", StringComparison.OrdinalIgnoreCase));
        names.Should().NotContain(n => n.Contains("BROKERAGE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Refresh_ticks_a_mortgage_row_from_the_paying_leg_and_ignores_the_receiving_leg_of_the_transfer()
    {
        var store = new InMemoryStore();
        var txns = new FakeTransactionRepository();
        var accounts = new FakeAccountRepository();
        var tenant = TenantId.New();
        var mortgage = Account.Create(tenant, "Mortgage", AccountType.Mortgage, new Money(200000m, "USD"), FlowLedger.TestSupport.TestClock.Provider);
        var checking = Account.Create(tenant, "Checking", AccountType.Checking, new Money(5000m, "USD"), FlowLedger.TestSupport.TestClock.Provider);
        await accounts.AddAsync(mortgage);
        await accounts.AddAsync(checking);
        var svc = new BillSheetService(store, txns, FlowLedger.TestSupport.TestClock.Provider, accounts);

        var payingLegs = new List<Guid>();
        var receivingLegs = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var d = ThisMonth.AddMonths(-i).AddDays(9);
            var paying = Transaction.RecordActual(tenant, checking.AccountId, new Money(1500m, "USD"),
                TransactionDirection.Debit, "Harbor Home Loan", d, d, TransactionSource.MxAggregation, FlowLedger.TestSupport.TestClock.Provider);
            var receiving = Transaction.RecordActual(tenant, mortgage.AccountId, new Money(1500m, "USD"),
                TransactionDirection.Debit, "Harbor Home Loan", d, d, TransactionSource.MxAggregation, FlowLedger.TestSupport.TestClock.Provider);
            await txns.AddAsync(paying);
            await txns.AddAsync(receiving);
            payingLegs.Add(paying.Id);
            receivingLegs.Add(receiving.Id);
        }

        await svc.RefreshAsync();

        var bills = await store.ListBillsAsync(includeInactive: true);
        bills.Should().ContainSingle(b => b.Name.Contains("Harbor", StringComparison.OrdinalIgnoreCase), "the receiving leg is not a second recurring expense");
        var payments = await store.ListPaymentsAsync(ThisMonth.AddMonths(-6), ThisMonth);
        payments.Where(p => p.TransactionId is not null).Select(p => p.TransactionId!.Value)
            .Should().NotBeEmpty().And.OnlyContain(id => payingLegs.Contains(id) && !receivingLegs.Contains(id));
    }

    [Fact]
    public async Task Import_maps_headers_sections_totals_and_manual_marks()
    {
        var (svc, store, _) = Make();

        var result = await svc.ImportCsvAsync(ImportSheetRequestBuilder.A().OfCsv(SampleCsv()).Build());

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
        await svc.ImportCsvAsync(ImportSheetRequestBuilder.A().OfCsv(SampleCsv()).Build());

        var again = () => svc.ImportCsvAsync(ImportSheetRequestBuilder.A().OfCsv(SampleCsv()).Build());
        await again.Should().ThrowAsync<SheetOperationException>().WithMessage("*replace*");

        var replaced = await svc.ImportCsvAsync(ImportSheetRequestBuilder.A().OfCsv(SampleCsv()).Replacing().Build());
        replaced.Bills.Should().Be(4);
    }

    [Fact]
    public async Task View_has_per_section_and_grand_totals_and_masks_account_numbers()
    {
        var (svc, _, _) = Make();
        await svc.ImportCsvAsync(ImportSheetRequestBuilder.A().OfCsv(SampleCsv()).Build());

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
        await svc.ImportCsvAsync(ImportSheetRequestBuilder.A().OfCsv(SampleCsv()).Build());
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
        await svc.ImportCsvAsync(ImportSheetRequestBuilder.A().OfCsv(SampleCsv()).Build());
        var power = (await store.ListBillsAsync()).Single(b => b.Name == "Power Co");
        var cur = BillSheetService.Key(ThisMonth);
        await AddTxn(txns, "POWER CO", 400m, new DateOnly(Today.Year, Today.Month, Math.Min(4, DateTime.DaysInMonth(Today.Year, Today.Month))));

        await svc.SetPaymentAsync(power.Id, cur, SetPaymentRequestBuilder.A().Paid(false).Build());
        await svc.RefreshAsync();

        var view = await svc.GetViewAsync(cur, 1);
        view.Sections.SelectMany(s => s.Rows).Single(r => r.Name == "Power Co").Cells[cur].Paid.Should().BeFalse();

        await svc.SetPaymentAsync(power.Id, cur, SetPaymentRequestBuilder.A().Paid().ForAmount(123m).Build());
        await svc.RefreshAsync();
        var after = await svc.GetViewAsync(cur, 1);
        after.Sections.SelectMany(s => s.Rows).Single(r => r.Name == "Power Co").Cells[cur]
            .Should().Match<SheetCellDto>(c => c.Paid && c.Amount == 123m && c.Source == "Manual");
    }

    [Fact]
    public async Task Create_and_update_validate_input_and_keep_masked_account_ref()
    {
        var (svc, store, _) = Make();
        var bad = () => svc.CreateBillAsync(SaveBillRequestBuilder.A().Named(" ").Estimating(1).Build());
        await bad.Should().ThrowAsync<SheetOperationException>();

        var id = await svc.CreateBillAsync(SaveBillRequestBuilder.A().Named("Water").PaidBy("JD").Perpetual().AutoPaying().DueOn(14).Estimating(120).WithAccountRef("108260316").Build());
        var current = (await store.ListBillsAsync()).Single();
        BillSheetService.MaskReference(current.AccountRef).Should().Be("••••0316");

        await svc.UpdateBillAsync(id, SaveBillRequestBuilder.A().Named("Water").PaidBy("JD").Perpetual().AutoPaying().DueOn(14).Estimating(125).WithAccountRef("••••0316").MatchingText("city of tulsa").Build());

        var updated = (await store.ListBillsAsync()).Single();
        updated.AccountRef.Should().Be("108260316");
        updated.MatchText.Should().Be("city of tulsa");
    }

    [Fact]
    public async Task Importing_another_tab_keeps_existing_sections_and_replace_is_per_section()
    {
        var (svc, store, _) = Make();
        await svc.ImportCsvAsync(ImportSheetRequestBuilder.A().OfCsv(SampleCsv()).Build());
        const string cards = " ,Payer,Perpetual,Optional,Business/Personal,Service,Auto/Manual,Account Number,Due Date,Estimated Cost,Actual Cost\n" +
            "Credit Cards,JD,No,No,,,,,,,\n" +
            "Amazon,JD,No,No,,,,,5th,0,0\n";
        var csv = cards.Replace("Credit Cards,JD,No,No,,,,,,,\n", "");
        csv = csv.Replace(" ,Payer", "Credit Cards,Payer");

        var result = await svc.ImportCsvAsync(ImportSheetRequestBuilder.A().OfCsv(csv).Build());

        result.Bills.Should().Be(1);
        (await store.ListBillsAsync()).Should().HaveCount(5);

        var again = () => svc.ImportCsvAsync(ImportSheetRequestBuilder.A().OfCsv(csv).Build());
        await again.Should().ThrowAsync<SheetOperationException>().WithMessage("*Credit Cards*");
        await svc.ImportCsvAsync(ImportSheetRequestBuilder.A().OfCsv(csv).Replacing().Build());
        (await store.ListBillsAsync()).Should().HaveCount(5);
    }

    [Fact]
    public async Task Import_tolerates_data_rows_wider_than_the_header()
    {
        var (svc, store, _) = Make();
        const string csv = "Credit Cards,Payer,Perpetual,Optional,Business/Personal,Service,Auto/Manual,Account Number,Due Date,Estimated Cost,Actual Cost\n" +
            "LL Flooring,JD,No,No,,,,,,520,520,x\n";

        var result = await svc.ImportCsvAsync(ImportSheetRequestBuilder.A().OfCsv(csv).Build());

        result.Bills.Should().Be(1);
        (await store.ListBillsAsync()).Single().Estimated.Should().Be(520m);
    }

    [Fact]
    public async Task Refresh_builds_rows_from_recurring_expenses_ticks_them_and_respects_deletes()
    {
        var (svc, store, txns) = Make();
        for (var back = 0; back < 3; back++)
        {
            var month = ThisMonth.AddMonths(-back);
            await AddTxn(txns, "Peak Fitness Club", 39.99m, new DateOnly(month.Year, month.Month, 9));
        }

        var first = await svc.RefreshAsync();

        first.Added.Should().Be(1);
        first.Marked.Should().Be(3);
        var row = (await store.ListBillsAsync()).Single();
        row.Should().Match<SheetBillData>(b => b.Section == "Detected" && b.Name == "Peak Fitness Club" && b.DueDay == 9 && b.Estimated == 39.99m);

        // Idempotent, and a row the user deleted is not rebuilt.
        (await svc.RefreshAsync()).Added.Should().Be(0);
        await svc.DeleteBillAsync(row.Id);
        (await svc.RefreshAsync()).Added.Should().Be(0);
        (await store.ListBillsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Detection_does_not_duplicate_a_row_the_sheet_already_has()
    {
        var (svc, store, txns) = Make();
        await svc.CreateBillAsync(SaveBillRequestBuilder.A().Named("Peak Fitness").PaidBy("JD").Perpetual().AutoPaying().DueOn(9).Estimating(40).Build());
        for (var back = 0; back < 3; back++)
        {
            var month = ThisMonth.AddMonths(-back);
            await AddTxn(txns, "Peak Fitness Club", 39.99m, new DateOnly(month.Year, month.Month, 9));
        }

        var r = await svc.RefreshAsync();

        r.Added.Should().Be(0);
        r.Marked.Should().Be(3);
        (await store.ListBillsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Clear_returns_a_cell_to_automatic_and_hint_flags_unmatched_rows()
    {
        var (svc, store, _) = Make();
        await svc.ImportCsvAsync(ImportSheetRequestBuilder.A().OfCsv(SampleCsv()).Build());
        var power = (await store.ListBillsAsync()).Single(b => b.Name == "Power Co");
        var cur = BillSheetService.Key(ThisMonth);

        await svc.SetPaymentAsync(power.Id, cur, SetPaymentRequestBuilder.A().Paid().ForAmount(5m).Build());
        await svc.ClearPaymentAsync(power.Id, cur);

        var view = await svc.GetViewAsync(cur, 1);
        var row = view.Sections.SelectMany(x => x.Rows).Single(r => r.Name == "Power Co");
        row.Cells.Should().NotContainKey(cur);
        row.NeedsMatchText.Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_detects_a_fortnightly_bill_and_the_month_counts_every_mow()
    {
        var (svc, store, txns) = Make();
        for (var i = 0; i < 8; i++)
        {
            await AddTxn(txns, "MUSTANG MOWING", 55m, Today.AddDays(-3 - 14 * i));
        }

        var result = await svc.RefreshAsync();

        result.Added.Should().Be(1);
        var bill = (await store.ListBillsAsync()).Single();
        bill.Should().Match<SheetBillData>(b => b.Frequency == BillFrequency.EveryNWeeks && b.IntervalWeeks == 2 && b.Estimated == 55m);

        // Every month in view lists the mows that fall in it, each payment ticked from its own transaction.
        var view = await svc.GetViewAsync(BillSheetService.Key(ThisMonth), 3);
        var row = view.Sections.SelectMany(x => x.Rows).Single();
        foreach (var month in view.Months)
        {
            var slots = row.Slots![month];
            slots.Count.Should().BeInRange(2, 3);
            slots.Where(sl => sl.Due <= Today.AddDays(-3)).Should().OnlyContain(sl => sl.Paid);
        }

        var selectedSlots = row.Slots![view.SelectedMonth].Count;
        row.MonthlyEstimated.Should().Be(55m * selectedSlots);
        view.GrandTotals.Estimated.Total.Should().Be(55m * selectedSlots);
        view.Summary.TotalCount.Should().Be(selectedSlots);
    }

    [Fact]
    public async Task Refresh_upgrades_a_monthly_detected_row_that_actually_repeats()
    {
        var (svc, store, txns) = Make();
        var monthly = new SheetBillData(Guid.NewGuid(), BillSheetService.DetectedSection, 0, "MUSTANG MOWING", null, false, true, false,
            false, false, 12, 55m, 55m, "Detected from your transactions", null, "MUSTANG MOWING", true);
        await store.SaveBillAsync(monthly);
        for (var i = 0; i < 8; i++)
        {
            await AddTxn(txns, "MUSTANG MOWING", 55m, Today.AddDays(-3 - 14 * i));
        }

        await svc.RefreshAsync();

        (await store.ListBillsAsync()).Single().Should().Match<SheetBillData>(b =>
            b.Id == monthly.Id && b.Frequency == BillFrequency.EveryNWeeks && b.DueDay == null && b.AnchorDate != null);
    }

    private static async Task AddTxn(FakeTransactionRepository repo, string desc, decimal amount, DateOnly date)
    {
        var tx = Transaction.RecordActual(
            TenantId.New(), AccountId.New(), new Money(amount, "USD"), TransactionDirection.Debit, desc, date, date,
            TransactionSource.MxAggregation, FlowLedger.TestSupport.TestClock.Provider);
        await repo.AddAsync(tx);
    }

    private sealed class InMemoryStore : IBillSheetStore
    {
        private readonly List<SheetBillData> _bills = [];
        private readonly List<SheetPaymentData> _payments = [];

        public Task<IReadOnlyList<SheetBillData>> ListBillsAsync(bool includeInactive = false, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SheetBillData>>(_bills.Where(b => includeInactive || b.IsActive).OrderBy(b => b.Section).ThenBy(b => b.SortOrder).ToList());

        public Task<SheetBillData?> GetBillAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_bills.FirstOrDefault(b => b.Id == id && b.IsActive));

        public Task SaveBillAsync(SheetBillData bill, CancellationToken ct = default)
        {
            _bills.RemoveAll(b => b.Id == bill.Id);
            _bills.Add(bill);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteBillAsync(Guid id, CancellationToken ct = default)
        {
            _payments.RemoveAll(p => p.BillId == id);
            var i = _bills.FindIndex(b => b.Id == id && b.IsActive);
            if (i >= 0)
            {
                _bills[i] = _bills[i] with { IsActive = false }; // soft delete, like the EF store
            }

            return Task.FromResult(i >= 0);
        }

        public Task<IReadOnlyList<SheetPaymentData>> ListPaymentsAsync(DateOnly fromMonth, DateOnly toMonth, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SheetPaymentData>>(_payments.Where(p => p.Month >= fromMonth && p.Month <= toMonth).ToList());

        public Task ApplyPaymentsAsync(IReadOnlyList<SheetPaymentData> upserts, IReadOnlyList<(Guid BillId, DateOnly Month, int Slot)> clears, CancellationToken ct = default)
        {
            foreach (var (b, m, slot) in clears)
            {
                _payments.RemoveAll(p => p.BillId == b && p.Month == m && (slot < 0 || p.Slot == slot));
            }

            foreach (var u in upserts)
            {
                _payments.RemoveAll(p => p.BillId == u.BillId && p.Month == u.Month && p.Slot == u.Slot);
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
