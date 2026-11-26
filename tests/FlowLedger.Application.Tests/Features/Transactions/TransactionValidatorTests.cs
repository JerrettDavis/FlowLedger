using FlowLedger.Application.Features.Transactions;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features.Transactions;

public sealed class TransactionValidatorTests
{
    private readonly CreateTransactionRequestValidator _validator = new();

    [Fact]
    public async Task ValidRequest_PassesValidation()
    {
        var req = CreateTransactionRequestBuilder.A().Build();
        var result = await _validator.ValidateAsync(req);
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]     // zero amount
    [InlineData(-50)]   // negative amount
    public async Task ZeroOrNegativeAmount_FailsValidation(decimal amount)
    {
        var req = CreateTransactionRequestBuilder.A().WithAmount(amount).Build();
        var result = await _validator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task EmptyDescription_FailsValidation()
    {
        var req = CreateTransactionRequestBuilder.A().Described("").Build();
        var result = await _validator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task InvalidDirection_FailsValidation()
    {
        var req = CreateTransactionRequestBuilder.A().WithDirection("Transfer").Build();
        var result = await _validator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task InvalidCurrency_FailsValidation()
    {
        var req = CreateTransactionRequestBuilder.A().InCurrency("US").Build();
        var result = await _validator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
    }
}
