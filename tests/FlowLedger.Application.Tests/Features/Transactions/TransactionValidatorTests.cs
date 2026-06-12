using FlowLedger.Application.Features.Transactions;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features.Transactions;

public sealed class TransactionValidatorTests
{
    private readonly CreateTransactionRequestValidator _validator = new();

    [Fact]
    public async Task ValidRequest_PassesValidation()
    {
        var req = new CreateTransactionRequest(
            Guid.NewGuid(), 100m, "USD", "Debit",
            "Test merchant", new DateOnly(2026, 1, 1), null, null, null, null);
        var result = await _validator.ValidateAsync(req);
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]     // zero amount
    [InlineData(-50)]   // negative amount
    public async Task ZeroOrNegativeAmount_FailsValidation(decimal amount)
    {
        var req = new CreateTransactionRequest(
            Guid.NewGuid(), amount, "USD", "Debit",
            "Test", new DateOnly(2026, 1, 1), null, null, null, null);
        var result = await _validator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task EmptyDescription_FailsValidation()
    {
        var req = new CreateTransactionRequest(
            Guid.NewGuid(), 100m, "USD", "Debit",
            "", new DateOnly(2026, 1, 1), null, null, null, null);
        var result = await _validator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task InvalidDirection_FailsValidation()
    {
        var req = new CreateTransactionRequest(
            Guid.NewGuid(), 100m, "USD", "Transfer",
            "Test", new DateOnly(2026, 1, 1), null, null, null, null);
        var result = await _validator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task InvalidCurrency_FailsValidation()
    {
        var req = new CreateTransactionRequest(
            Guid.NewGuid(), 100m, "US", "Debit",
            "Test", new DateOnly(2026, 1, 1), null, null, null, null);
        var result = await _validator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
    }
}
