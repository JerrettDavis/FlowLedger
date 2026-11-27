using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx.Contracts;
using FlowLedger.Integrations.Mx.Mapping;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Integrations.Tests.Mx;

/// <summary>
/// Pure mapping unit tests for <see cref="MxMapper"/> and <see cref="MxCursor"/> — no HTTP.
/// Covers account/transaction translation, connection-status mapping, and cursor
/// encode / advance / stability.
/// </summary>
public sealed class MxMapperTests
{
    // ── Account mapping ──────────────────────────────────────────────────────────

    [Fact]
    public void ToProviderAccount_maps_core_fields()
    {
        var mx = new MxAccount("ACT-1", "Checking", "CHECKING", "NONE",
            Balance: 100.50m, AvailableBalance: 90m, CurrencyCode: "USD",
            MemberGuid: "MBR-1", UserGuid: "USR-1");

        var dto = MxMapper.ToProviderAccount(mx);

        dto.ProviderId.Should().Be("ACT-1");
        dto.Name.Should().Be("Checking");
        dto.AccountType.Should().Be("CHECKING");
        dto.Balance.Amount.Should().Be(100.50m);
        dto.Balance.Currency.Code.Should().Be("USD");
        dto.AvailableBalance!.Amount.Should().Be(90m);
        dto.CurrencyCode.Should().Be("USD");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("XY")]
    [InlineData("US1")]
    public void ToProviderAccount_does_not_default_to_USD_when_currency_is_missing_or_invalid(string? code)
    {
        var mx = new MxAccount("ACT-1", "X", "CHECKING", null, 1m, null, code, "MBR-1", "USR-1");

        var act = () => MxMapper.ToProviderAccount(mx);

        act.Should().Throw<UnknownCurrencyException>();
    }

    private static MxTransaction Tx(string? currency) =>
        new("TXN-1", "ACT-1", 5m, currency, "2026-01-02", null, null, "Coffee", null, null, null, null, "POSTED", "DEBIT");

    [Fact]
    public void ToProviderTransaction_inherits_the_account_currency_when_it_has_none()
    {
        MxMapper.ToProviderTransaction(Tx(null), null, "EUR").Amount.Currency.Code.Should().Be("EUR");
        MxMapper.ToProviderTransaction(Tx("gbp"), null, "EUR").Amount.Currency.Code.Should().Be("GBP", "its own currency wins");
    }

    [Fact]
    public void ToProviderTransaction_does_not_default_to_USD_when_the_account_currency_is_unknown_too()
    {
        var act = () => MxMapper.ToProviderTransaction(Tx(null));

        act.Should().Throw<UnknownCurrencyException>();
    }

    [Fact]
    public void ToProviderAccount_normalises_lowercase_currency()
    {
        var mx = new MxAccount("ACT-1", "X", "CHECKING", null, 1m, null, " eur ", "MBR-1", "USR-1");

        MxMapper.ToProviderAccount(mx).CurrencyCode.Should().Be("EUR");
    }

    [Fact]
    public void ToProviderAccount_throws_fatal_when_guid_missing()
    {
        var mx = new MxAccount(null, "X", "CHECKING", null, 1m, null, "USD", "MBR-1", "USR-1");

        var act = () => MxMapper.ToProviderAccount(mx);

        act.Should().Throw<FatalProviderException>();
    }

    [Theory]
    [InlineData("CHECKING", "CHECKING")]
    [InlineData("savings", "SAVINGS")]
    [InlineData("CREDIT_CARD", "CREDIT_CARD")]
    [InlineData("LINE_OF_CREDIT", "CREDIT_CARD")]
    [InlineData("LOAN", "LOAN")]
    [InlineData("MORTGAGE", "MORTGAGE")]
    [InlineData("INVESTMENT", "INVESTMENT")]
    [InlineData("PREPAID", "CASH")]
    [InlineData("something-unknown", "CHECKING")]
    [InlineData(null, "CHECKING")]
    public void NormalizeAccountType_maps_known_and_unknown(string? input, string expected)
    {
        MxMapper.NormalizeAccountType(input).Should().Be(expected);
    }

    // ── Transaction mapping ──────────────────────────────────────────────────────

    [Fact]
    public void ToProviderTransaction_debit_is_negative_credit_is_positive()
    {
        var debit = MxMapper.ToProviderTransaction(MakeTx("DEBIT", 25m));
        var credit = MxMapper.ToProviderTransaction(MakeTx("CREDIT", 25m));

        debit.Amount.Amount.Should().Be(-25m);
        credit.Amount.Amount.Should().Be(25m);
    }

    [Fact]
    public void ToProviderTransaction_carries_the_MX_category_as_the_provider_hint()
    {
        MxMapper.ToProviderTransaction(MakeTx("DEBIT", 5m)).ProviderCategory.Should().Be("Shopping");
    }

    [Fact]
    public void ToProviderTransaction_pending_flag_from_status()
    {
        var pending = MxMapper.ToProviderTransaction(MakeTx("DEBIT", 5m, status: "PENDING"));
        var posted = MxMapper.ToProviderTransaction(MakeTx("DEBIT", 5m, status: "POSTED"));

        pending.IsPending.Should().BeTrue();
        posted.IsPending.Should().BeFalse();
    }

    [Fact]
    public void ToProviderTransaction_uses_original_description_as_raw()
    {
        var dto = MxMapper.ToProviderTransaction(MakeTx("DEBIT", 5m));

        dto.RawDescription.Should().Be("ORIGINAL RAW");
        dto.MerchantName.Should().Be("Clean Merchant");
        dto.PostedDate.Should().Be(new DateOnly(2026, 1, 15));
    }

    [Fact]
    public void ToProviderTransaction_throws_fatal_when_account_guid_missing()
    {
        var tx = MakeTx("DEBIT", 5m) with { AccountGuid = null };

        var act = () => MxMapper.ToProviderTransaction(tx);

        act.Should().Throw<FatalProviderException>();
    }

    // ── Connection-status mapping ─────────────────────────────────────────────────

    [Theory]
    [InlineData("CONNECTED", ConnectionStatus.Healthy)]
    [InlineData("CREATED", ConnectionStatus.ConnectionPending)]
    [InlineData("CHALLENGED", ConnectionStatus.NeedsUserAction)]
    [InlineData("DENIED", ConnectionStatus.NeedsUserAction)]
    [InlineData("EXPIRED", ConnectionStatus.NeedsUserAction)]
    [InlineData("DEGRADED", ConnectionStatus.Degraded)]
    [InlineData("DISABLED", ConnectionStatus.Disabled)]
    [InlineData("DISCONNECTED", ConnectionStatus.Error)]
    [InlineData("FAILED", ConnectionStatus.Error)]
    [InlineData("totally-unknown", ConnectionStatus.ConnectionPending)]
    public void ToConnectionStatus_maps_member_states(string mx, ConnectionStatus expected)
    {
        MxMapper.ToConnectionStatus(mx).Should().Be(expected);
    }

    [Theory]
    [InlineData("CHALLENGED", true)]
    [InlineData("LOCKED", true)]
    [InlineData("CONNECTED", false)]
    [InlineData("DEGRADED", false)]
    public void RequiresUserAction_flags_only_user_action_states(string mx, bool expected)
    {
        MxMapper.RequiresUserAction(mx).Should().Be(expected);
    }

    // ── Cursor encode / advance / stability ───────────────────────────────────────

    [Fact]
    public void Cursor_initial_decodes_to_page_one()
    {
        var c = MxCursor.Decode(SyncCursor.Initial, fallbackRecordsPerPage: 50);

        c.Page.Should().Be(1);
        c.RecordsPerPage.Should().Be(50);
    }

    [Fact]
    public void Cursor_encode_then_decode_round_trips()
    {
        var original = new MxCursor(3, 75);

        var decoded = MxCursor.Decode(original.Encode(), fallbackRecordsPerPage: 10);

        decoded.Should().Be(original);
    }

    [Fact]
    public void Cursor_encode_is_deterministic_stable()
    {
        var a = new MxCursor(2, 100).Encode();
        var b = new MxCursor(2, 100).Encode();

        a.Value.Should().Be(b.Value);
    }

    [Fact]
    public void Cursor_next_advances_page_and_changes_encoding()
    {
        var first = new MxCursor(1, 100);
        var second = first.Next();

        second.Page.Should().Be(2);
        second.Encode().Value.Should().NotBe(first.Encode().Value);
    }

    [Fact]
    public void Cursor_malformed_value_falls_back_to_page_one()
    {
        var c = MxCursor.Decode(new SyncCursor("not-base64-!@#"), fallbackRecordsPerPage: 25);

        c.Page.Should().Be(1);
        c.RecordsPerPage.Should().Be(25);
    }

    [Fact]
    public void ToProviderTransaction_with_all_unparseable_dates_returns_MinValue_and_logs_warning()
    {
        var logger = new CapturingLogger();
        var tx = MakeTx("DEBIT", 5m) with
        {
            Date = null,
            PostedAt = "invalid-date",
            TransactedAt = "also-invalid",
        };

        var dto = MxMapper.ToProviderTransaction(tx, logger);

        dto.PostedDate.Should().Be(DateOnly.MinValue);
        logger.HasWarning.Should().BeTrue("a warning must be logged for unparseable dates");
    }

    [Fact]
    public void ToProviderTransaction_with_valid_date_prefers_date_field()
    {
        var logger = new CapturingLogger();
        var tx = MakeTx("DEBIT", 5m) with
        {
            Date = "2026-03-20",
            PostedAt = "2026-01-10T12:00:00Z",
        };

        var dto = MxMapper.ToProviderTransaction(tx, logger);

        dto.PostedDate.Should().Be(new DateOnly(2026, 3, 20), "date field takes precedence");
        logger.HasWarning.Should().BeFalse();
    }

    [Fact]
    public void ToProviderTransaction_falls_back_to_posted_at_when_date_invalid()
    {
        var logger = new CapturingLogger();
        var tx = MakeTx("DEBIT", 5m) with
        {
            Date = null,
            PostedAt = "2026-02-14T08:30:00Z",
        };

        var dto = MxMapper.ToProviderTransaction(tx, logger);

        dto.PostedDate.Should().Be(new DateOnly(2026, 2, 14));
        logger.HasWarning.Should().BeFalse();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="ILogger"/> that captures whether any Warning-or-above message
    /// was emitted. Avoids a heavyweight mocking dependency.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public bool HasWarning { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                HasWarning = true;
            }
        }
    }

    private static MxTransaction MakeTx(string type, decimal amount, string status = "POSTED") =>
        new(
            Guid: "TRN-1",
            AccountGuid: "ACT-1",
            Amount: amount,
            CurrencyCode: "USD",
            Date: "2026-01-15",
            PostedAt: "2026-01-15T12:00:00Z",
            TransactedAt: "2026-01-15T12:00:00Z",
            Description: "Clean Merchant",
            OriginalDescription: "ORIGINAL RAW",
            MerchantGuid: "MCH-1",
            Category: "Shopping",
            TopLevelCategory: "Expenses",
            Status: status,
            Type: type);
}
