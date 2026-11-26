using CsCheck;
using FlowLedger.Domain.Exceptions;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.TestSupport;
using FluentAssertions;

namespace FlowLedger.Domain.Tests.Properties;

/// <summary>
/// Property-based tests (CsCheck, 1000 cases each, fixed starting seed) for the money, recurrence and fingerprint rules
/// that example tests cannot cover exhaustively. See <see cref="Prop"/> for how to replay a failing seed.
/// </summary>
public sealed class DomainPropertyTests
{
    // ── Money ────────────────────────────────────────────────────────────────

    [Fact]
    public void Money_add_then_subtract_round_trips()
    {
        var gen = Gen.Select(Gens.Currency, Gens.Amount, Gens.Amount, (c, a, b) => (m1: new Money(a, c), m2: new Money(b, c)));
        Prop.ForAll(gen, t => (t.m1 + t.m2 - t.m2) == t.m1);
    }

    [Fact]
    public void Money_addition_is_commutative_and_associative_with_zero_identity()
    {
        var gen = Gen.Select(Gens.Currency, Gens.Amount, Gens.Amount, Gens.Amount,
            (c, a, b, d) => (x: new Money(a, c), y: new Money(b, c), z: new Money(d, c)));
        Prop.ForAll(gen, t =>
            (t.x + t.y) == (t.y + t.x)
            && ((t.x + t.y) + t.z) == (t.x + (t.y + t.z))
            && (t.x + Money.Zero(t.x.Currency)) == t.x);
    }

    [Fact]
    public void Money_negate_is_an_involution_and_abs_is_never_negative()
    {
        Prop.ForAll(Gens.Money, m => m.Negate().Negate() == m && !m.Abs().IsNegative && (m + m.Negate()).IsZero);
    }

    [Fact]
    public void Money_arithmetic_across_currencies_always_throws()
    {
        var gen = Gen.Select(Gens.Currency, Gens.Currency, Gens.Amount, Gens.Amount, (c1, c2, a, b) => (a: new Money(a, c1), b: new Money(b, c2)))
            .Where(t => t.a.Currency != t.b.Currency);
        Prop.ForAll(gen, t =>
        {
            Action add = () => _ = t.a + t.b;
            Action sub = () => _ = t.a - t.b;
            Action cmp = () => _ = t.a > t.b;
            add.Should().Throw<CurrencyMismatchException>();
            sub.Should().Throw<CurrencyMismatchException>();
            cmp.Should().Throw<CurrencyMismatchException>();
        });
    }

    [Fact]
    public void Money_summing_in_any_order_gives_the_same_total_with_no_rounding_drift()
    {
        // decimal is exact for 2dp values: a permutation of the same cents must always sum identically.
        var gen = Gens.Amount.List[2, 40].SelectMany(list => Gen.Shuffle(list.ToArray()).Select(shuffled => (list, shuffled)));
        Prop.ForAll(gen, t =>
            t.list.Aggregate(Money.Zero("USD"), (acc, a) => acc + new Money(a, "USD"))
            == t.shuffled.Aggregate(Money.Zero("USD"), (acc, a) => acc + new Money(a, "USD")));
    }

    // ── RecurrenceExpander ───────────────────────────────────────────────────

    private static readonly Gen<(RecurrencePattern Pattern, DateOnly FlowStart, DateOnly? FlowEnd, DateOnly HorizonStart, DateOnly HorizonEnd)> ExpansionCase =
        Gen.Select(Gens.Pattern, Gens.Date, Gen.Int[-60, 900], Gen.Int[0, 800], Gen.Int[-400, 400], Gen.Bool,
            (pattern, flowStart, endOffset, horizonLen, horizonShift, hasEnd) =>
            {
                var horizonStart = flowStart.AddDays(horizonShift);
                DateOnly? flowEnd = hasEnd ? flowStart.AddDays(endOffset) : null;
                return (pattern, flowStart, flowEnd, horizonStart, horizonStart.AddDays(horizonLen));
            });

    private static IReadOnlyList<DateOnly> Run((RecurrencePattern Pattern, DateOnly FlowStart, DateOnly? FlowEnd, DateOnly HorizonStart, DateOnly HorizonEnd) c) =>
        RecurrenceExpander.Expand(c.Pattern, c.FlowStart, c.FlowEnd, new DateOnlyRange(c.HorizonStart, c.HorizonEnd));

    [Fact]
    public void Recurrence_output_is_strictly_increasing_and_inside_the_active_window()
    {
        Prop.ForAll(ExpansionCase, c =>
        {
            var dates = Run(c);
            for (var i = 0; i < dates.Count; i++)
            {
                dates[i].Should().BeOnOrAfter(c.FlowStart).And.BeOnOrAfter(c.HorizonStart).And.BeOnOrBefore(c.HorizonEnd);
                if (c.FlowEnd is { } end)
                {
                    dates[i].Should().BeBefore(end, "flowEnd is exclusive");
                }

                if (i > 0)
                {
                    dates[i].Should().BeAfter(dates[i - 1], "no duplicates and ascending");
                }
            }
        });
    }

    [Fact]
    public void Recurrence_is_deterministic_and_bounded_by_the_window_length()
    {
        Prop.ForAll(ExpansionCase, c =>
        {
            var first = Run(c);
            var second = Run(c);
            first.Should().Equal(second);
            first.Count.Should().BeLessThanOrEqualTo(c.HorizonEnd.DayNumber - c.HorizonStart.DayNumber + 1);
        });
    }

    [Fact]
    public void Recurrence_matches_a_day_by_day_reference_implementation()
    {
        // Covers month-end clamping (day 31 in 30-day months, Feb 29/28), leap years and last-business-day.
        Prop.ForAll(ExpansionCase, c =>
        {
            var expected = NaiveRecurrence.Expand(c.Pattern, c.FlowStart, c.FlowEnd, c.HorizonStart, c.HorizonEnd);
            Run(c).Should().Equal(expected, because: $"pattern {c.Pattern}, start {c.FlowStart}, end {c.FlowEnd}, horizon {c.HorizonStart}..{c.HorizonEnd}");
        });
    }

    [Fact]
    public void Monthly_recurrence_fires_exactly_once_per_calendar_month_inside_a_whole_month_window()
    {
        var gen = Gen.Select(Gen.Int[1, 31], Gen.Int[2019, 2032], Gen.Int[1, 12], (day, y, m) => (day, y, m));
        Prop.ForAll(gen, t =>
        {
            var first = new DateOnly(t.y, t.m, 1);
            var last = new DateOnly(t.y, t.m, DateTime.DaysInMonth(t.y, t.m));
            var dates = RecurrenceExpander.Expand(RecurrencePattern.Monthly(t.day), new DateOnly(2000, 1, 1), null, new DateOnlyRange(first, last));
            dates.Should().ContainSingle().Which.Day.Should().Be(Math.Min(t.day, last.Day));
        });
    }

    // ── TransactionFingerprint ───────────────────────────────────────────────

    private static readonly Gen<string> Word = Gen.Char["abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"].Array[1, 8].Select(a => new string(a));

    private static readonly Gen<(string Canonical, string Noisy)> DescriptionPair =
        Gen.Select(Word.List[1, 5], Gen.Int[1, 4].List[0, 6], Gen.Int[0, 3], Gen.Int[0, 3],
            (words, gaps, lead, trail) =>
            {
                var canonical = string.Join(' ', words);
                var spaces = new[] { " ", "  ", "\t", " \n ", "   " };
                var noisy = new string(' ', lead)
                    + string.Concat(words.Select((w, i) => (i == 0 ? "" : spaces[gaps.Count == 0 ? 0 : gaps[i % gaps.Count]]) + (i % 2 == 0 ? w.ToLowerInvariant() : w)))
                    + new string('\t', trail);
                return (canonical, noisy);
            });

    [Fact]
    public void Fingerprint_is_stable_under_whitespace_and_case_noise()
    {
        var gen = Gen.Select(DescriptionPair, Gens.Date, Gens.Amount, Gen.Guid, (p, date, amount, acct) => (p, date, amount, acct));
        Prop.ForAll(gen, t =>
        {
            var account = AccountId.From(t.acct);
            var clean = TransactionFingerprint.FromRawDescription(account, t.date, t.amount, t.p.Canonical);
            var noisy = TransactionFingerprint.FromRawDescription(account, t.date, t.amount, t.p.Noisy);
            noisy.Should().Be(clean);
        });
    }

    [Fact]
    public void Fingerprint_normalisation_is_idempotent_and_distinguishes_account_date_and_amount()
    {
        var gen = Gen.Select(Word, Gens.Date, Gens.Amount, Gen.Guid, Gen.Guid, (w, date, amount, a1, a2) => (w, date, amount, a1, a2));
        Prop.ForAll(gen, t =>
        {
            var once = TransactionFingerprint.NormalizeDescription(t.w);
            TransactionFingerprint.NormalizeDescription(once).Should().Be(once);

            var baseline = TransactionFingerprint.FromRawDescription(AccountId.From(t.a1), t.date, t.amount, t.w);
            TransactionFingerprint.FromRawDescription(AccountId.From(t.a1), t.date.AddDays(1), t.amount, t.w).Should().NotBe(baseline);
            TransactionFingerprint.FromRawDescription(AccountId.From(t.a1), t.date, t.amount + 0.01m, t.w).Should().NotBe(baseline);
            if (t.a1 != t.a2)
            {
                TransactionFingerprint.FromRawDescription(AccountId.From(t.a2), t.date, t.amount, t.w).Should().NotBe(baseline);
            }
        });
    }
}
