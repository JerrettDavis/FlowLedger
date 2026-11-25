using FlowLedger.Application.Features.Duplicates;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Duplicates;

public class DuplicateDetectorTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly AccountId Acct = AccountId.New();

    private static Transaction Tx(string desc, decimal amount, DateOnly date, string? pid = null, bool pending = false,
        TransactionDirection dir = TransactionDirection.Debit, AccountId? account = null)
    {
        var acct = account ?? Acct;
        var fp = TransactionFingerprint.FromRawDescription(acct, date, amount, desc, pid);
        return Transaction.RecordActual(Tenant, acct, new Money(amount, "USD"), dir, desc, date, pending ? null : date, TransactionSource.Manual, fp);
    }

    private static readonly DateOnly D = new(2026, 11, 10);

    [Fact]
    public void Imported_row_matching_a_bank_row_is_a_high_confidence_duplicate_and_the_bank_row_survives()
    {
        var bank = Tx("SQ *COFFEE HOUSE TULSA OK", 12.50m, D, pid: "TRN-1");
        var csv = Tx("Coffee House Tulsa", 12.50m, D.AddDays(1));

        var pairs = DuplicateDetector.Find([bank, csv]);

        var p = pairs.Should().ContainSingle().Subject;
        p.Confidence.Should().Be(DuplicateConfidence.High);
        p.Survivor.Should().BeSameAs(bank);
        p.Duplicate.Should().BeSameAs(csv);
    }

    [Fact]
    public void Two_rows_the_bank_issued_different_ids_are_never_high_confidence()
    {
        var a = Tx("365 VEND DDA PIN POS PUR", 2.60m, D, pid: "TRN-1");
        var b = Tx("365 VEND DDA PIN POS PUR", 2.60m, D, pid: "TRN-2");

        var p = DuplicateDetector.Find([a, b]).Should().ContainSingle().Subject;
        p.Confidence.Should().Be(DuplicateConfidence.Possible);
    }

    [Fact]
    public void Two_bank_rows_on_different_days_with_different_ids_are_not_flagged()
    {
        DuplicateDetector.Find([Tx("365 VEND", 2.60m, D, pid: "A"), Tx("365 VEND", 2.60m, D.AddDays(2), pid: "B")]).Should().BeEmpty();
    }

    [Fact]
    public void Pending_ghost_of_a_posted_row_is_a_high_confidence_duplicate()
    {
        var pending = Tx("SHELL OIL 5742", 40m, D, pid: "P-1", pending: true);
        var posted = Tx("SHELL OIL 5742", 40m, D.AddDays(2), pid: "P-2");

        var p = DuplicateDetector.Find([pending, posted]).Should().ContainSingle().Subject;
        p.Confidence.Should().Be(DuplicateConfidence.High);
        p.Survivor.Should().BeSameAs(posted);
    }

    [Fact]
    public void Different_accounts_amounts_or_directions_never_pair()
    {
        var other = AccountId.New();
        DuplicateDetector.Find([Tx("COFFEE", 5m, D), Tx("COFFEE", 5m, D, account: other)]).Should().BeEmpty();
        DuplicateDetector.Find([Tx("COFFEE", 5m, D), Tx("COFFEE", 5.01m, D)]).Should().BeEmpty();
        DuplicateDetector.Find([Tx("COFFEE", 5m, D), Tx("COFFEE", 5m, D, dir: TransactionDirection.Credit)]).Should().BeEmpty();
    }

    [Fact]
    public void Ambiguous_overlap_stays_a_suggestion()
    {
        // two real bank purchases and ONE imported row: it cannot be decided which the import duplicates
        var b1 = Tx("COFFEE HOUSE", 9m, D, pid: "1");
        var b2 = Tx("COFFEE HOUSE", 9m, D, pid: "2");
        var csv = Tx("COFFEE HOUSE", 9m, D);

        var pairs = DuplicateDetector.Find([b1, b2, csv]);

        pairs.Should().NotBeEmpty().And.OnlyContain(p => p.Confidence == DuplicateConfidence.Possible);
        pairs.Select(p => p.Duplicate).Should().NotContain(b => ReferenceEquals(b, b1) && ReferenceEquals(b, b2)); // each row joins at most one pair
        pairs.SelectMany(p => new[] { p.Survivor, p.Duplicate }).Distinct().Should().HaveCount(pairs.Count * 2);
    }

    [Fact]
    public void Unrelated_descriptions_with_the_same_amount_are_not_flagged()
    {
        DuplicateDetector.Find([Tx("SHELL OIL", 40m, D, pid: "1"), Tx("TARGET STORE", 40m, D)]).Should().BeEmpty();
    }

    [Fact]
    public void Planned_and_skipped_rows_are_ignored()
    {
        var planned = Transaction.CreatePlanned(Tenant, Acct, new Money(9m, "USD"), TransactionDirection.Debit, "RENT", D);
        DuplicateDetector.Find([planned, Tx("RENT", 9m, D)]).Should().BeEmpty();
    }

    [Fact]
    public void Pair_key_is_order_independent()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        DuplicatePair.MakeKey(a, b).Should().Be(DuplicatePair.MakeKey(b, a));
    }

    [Fact]
    public void User_categorised_row_survives_over_an_uncategorised_one()
    {
        var csv = Tx("COFFEE HOUSE", 9m, D);
        csv.Categorize(CategoryId.New(), null, CategorySource.Manual);
        var bank = Tx("COFFEE HOUSE", 9m, D, pid: "1");

        DuplicateDetector.Find([csv, bank]).Single().Survivor.Should().BeSameAs(csv);
    }
}
