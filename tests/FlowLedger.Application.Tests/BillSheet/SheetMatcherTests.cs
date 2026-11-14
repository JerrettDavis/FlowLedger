using FlowLedger.Application.Features.BillSheet;
using FluentAssertions;

namespace FlowLedger.Application.Tests.BillSheet;

public sealed class SheetMatcherTests
{
    private static readonly DateOnly Oct = new(2026, 10, 1);
    private static readonly DateOnly Nov = new(2026, 11, 1);

    private static SheetBillData Bill(string name, int? due, decimal est = 100m, string? match = null, int order = 0, Guid? id = null)
        => new(id ?? Guid.NewGuid(), "Bills", order, name, "JD", false, true, false, false, true, due, est, null, null, null, match, true);

    private static SheetTxn Txn(string text, DateOnly date, decimal amount = 100m, Guid? id = null)
        => new(id ?? Guid.NewGuid(), date, amount, text);

    private static SheetMatchResult Run(
        IEnumerable<SheetBillData> bills, IEnumerable<SheetTxn> txns,
        IEnumerable<SheetPaymentData>? existing = null, params DateOnly[] months)
        => SheetMatcher.Match([.. bills], [.. existing ?? []], [.. txns], months.Length == 0 ? [Oct, Nov] : months);

    [Fact]
    public void Marks_paid_when_text_matches_ignoring_case_and_punctuation()
    {
        var att = Bill("AT&T", 3, 310m);
        var r = Run([att], [Txn("ATT*BILL PAYMENT", new DateOnly(2026, 11, 3), 302.08m)]);

        r.Upserts.Should().ContainSingle();
        r.Upserts[0].Should().Match<SheetPaymentData>(p =>
            p.BillId == att.Id && p.Month == Nov && p.IsPaid && p.Amount == 302.08m && p.Source == SheetPaymentSource.Auto);
    }

    [Fact]
    public void Match_text_accepts_alternatives()
    {
        var aep = Bill("AEP (AMER ELECT PWR)", 4, 560m, match: "aep|public service of ok");
        var r = Run([aep], [Txn("PUBLIC SERVICE OF OK ONLINE", new DateOnly(2026, 11, 4), 410m)]);
        r.Upserts.Should().ContainSingle(p => p.BillId == aep.Id && p.Amount == 410m);
    }

    [Fact]
    public void Amount_is_not_a_filter_for_variable_bills()
    {
        var gas = Bill("Oklahoma Natural Gas", 4, 120m);
        var r = Run([gas], [Txn("OKLAHOMA NATURAL GAS", new DateOnly(2026, 11, 4), 340m)]);
        r.Upserts.Should().ContainSingle();
    }

    [Fact]
    public void Transaction_goes_to_nearest_month_when_two_cells_are_open()
    {
        // Mortgage due on the 1st: an Oct 30 payment belongs to Nov (2 days away), not Oct (29 days).
        var m = Bill("Rocket Mortgage", 1, 1300m);
        var r = Run([m], [Txn("ROCKET MORTGAGE", new DateOnly(2026, 10, 30), 1286.15m)]);
        r.Upserts.Should().ContainSingle(p => p.Month == Nov);
    }

    [Fact]
    public void Early_payment_leaves_the_other_cell_for_its_own_transaction()
    {
        var m = Bill("Rocket Mortgage", 1, 1300m);
        var r = Run([m], [
            Txn("ROCKET MORTGAGE", new DateOnly(2026, 10, 1), 1286m),
            Txn("ROCKET MORTGAGE", new DateOnly(2026, 10, 31), 1286m)]);
        r.Upserts.Select(p => p.Month).Should().BeEquivalentTo([Oct, Nov]);
    }

    [Fact]
    public void Transaction_beyond_seven_days_of_due_date_is_ignored()
    {
        var b = Bill("Netflix", 15);
        var r = Run([b], [Txn("NETFLIX", new DateOnly(2026, 11, 28))]);
        r.Upserts.Should().BeEmpty();
    }

    [Fact]
    public void Flux_bills_match_only_inside_the_month()
    {
        var b = Bill("Moxie Pest Control", null, match: "moxie pest");
        var r = Run([b], [Txn("MOXIE PEST", new DateOnly(2026, 12, 2)), Txn("MOXIE PEST", new DateOnly(2026, 11, 20))]);
        r.Upserts.Should().ContainSingle(p => p.Month == Nov);
    }

    [Fact]
    public void Identical_rows_each_get_one_transaction_and_runs_are_stable()
    {
        var bills = Enumerable.Range(0, 4)
            .Select(i => Bill("Emeregency Savings Deposit", null, 100m, match: "emergency savings", order: i)).ToList();
        var txns = Enumerable.Range(0, 4)
            .Select(i => Txn("TRANSFER EMERGENCY SAVINGS", new DateOnly(2026, 11, 2 + i), 100m)).ToList();

        var a = Run(bills, txns, months: Nov);
        var b = Run(bills, txns, months: Nov);

        // One cell per bill per month: 4 bills x 1 month -> 4 distinct transactions, one each.
        a.Upserts.Select(p => p.BillId).Should().BeEquivalentTo(bills.Select(x => x.Id));
        a.Upserts.Select(p => p.TransactionId).Distinct().Should().HaveCount(4);
        a.Upserts.Should().BeEquivalentTo(b.Upserts);
    }

    [Fact]
    public void One_transaction_pays_only_one_cell()
    {
        var b1 = Bill("Mowing", null, 50m, order: 0);
        var b2 = Bill("Mowing", null, 50m, order: 1);
        var r = Run([b1, b2], [Txn("MUSTANG MOWING", new DateOnly(2026, 11, 5), 50m, id: Guid.NewGuid())], months: Nov);
        r.Upserts.Should().ContainSingle(); // two identical rows, one transaction: only one cell is paid
    }

    [Fact]
    public void Manual_marks_are_never_changed_and_manual_unpaid_suppresses_automation()
    {
        var b = Bill("Netflix", 15);
        var suppressed = new SheetPaymentData(b.Id, Nov, false, null, null, null, SheetPaymentSource.Manual);
        var r = Run([b], [Txn("NETFLIX", new DateOnly(2026, 11, 15))], [suppressed], Nov);

        r.Upserts.Should().BeEmpty();
        r.Clears.Should().BeEmpty();
    }

    [Fact]
    public void Auto_mark_is_cleared_when_its_transaction_disappears_then_rematched()
    {
        var b = Bill("Netflix", 15);
        var gone = new SheetPaymentData(b.Id, Nov, true, 26.99m, new DateOnly(2026, 11, 15), Guid.NewGuid(), SheetPaymentSource.Auto);
        var replacement = Txn("NETFLIX", new DateOnly(2026, 11, 16), 26.99m);

        var r = Run([b], [replacement], [gone], Nov);

        r.Clears.Should().ContainSingle(c => c.BillId == b.Id && c.Month == Nov);
        r.Upserts.Should().ContainSingle(p => p.TransactionId == replacement.Id);
    }

    [Fact]
    public void Valid_auto_mark_is_kept_and_not_duplicated()
    {
        var b = Bill("Netflix", 15);
        var t = Txn("NETFLIX", new DateOnly(2026, 11, 15), 26.99m);
        var kept = new SheetPaymentData(b.Id, Nov, true, 26.99m, t.Date, t.Id, SheetPaymentSource.Auto);

        var r = Run([b], [t], [kept], Nov);

        r.Upserts.Should().BeEmpty();
        r.Clears.Should().BeEmpty();
    }

    [Fact]
    public void Auto_mark_cannot_steal_a_transaction_claimed_by_a_manual_mark()
    {
        var a = Bill("Citi", 8, order: 0);
        var b = Bill("Citi", 8, order: 1);
        var t = Txn("CITI CARD PAYMENT", new DateOnly(2026, 11, 8), 644.93m);
        var manual = new SheetPaymentData(a.Id, Nov, true, 644.93m, t.Date, t.Id, SheetPaymentSource.Manual);

        var r = Run([a, b], [t], [manual], Nov);

        r.Upserts.Should().BeEmpty();
    }

    [Fact]
    public void Due_day_is_clamped_to_short_months()
    {
        SheetMatcher.ExpectedDate(31, new DateOnly(2026, 2, 1)).Should().Be(new DateOnly(2026, 2, 28));
        SheetMatcher.ExpectedDate(null, Nov).Should().BeNull();
    }
}
