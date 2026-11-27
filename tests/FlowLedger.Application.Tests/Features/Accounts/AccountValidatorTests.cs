using FlowLedger.Application.Features.Accounts;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features.Accounts;

public sealed class AccountValidatorTests
{
    private readonly CreateAccountRequestValidator _createValidator = new();
    private readonly UpdateAccountRequestValidator _updateValidator = new();

    [Fact]
    public async Task CreateAccount_ValidRequest_PassesValidation()
    {
        var req = CreateAccountRequestBuilder.A().Build();
        var result = await _createValidator.ValidateAsync(req);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAccount_EmptyName_FailsValidation()
    {
        var req = CreateAccountRequestBuilder.A().Named("").Build();
        var result = await _createValidator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAccount_InvalidAccountType_FailsValidation()
    {
        var req = CreateAccountRequestBuilder.A().OfType("BadType").Build();
        var result = await _createValidator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAccount_NegativeBalance_FailsValidation()
    {
        var req = CreateAccountRequestBuilder.A().StartingAt(-1m).Build();
        var result = await _createValidator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAccount_BadCurrencyCode_FailsValidation()
    {
        var req = CreateAccountRequestBuilder.A().InCurrency("US").Build();
        var result = await _createValidator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAccount_EmptyName_FailsValidation()
    {
        var req = UpdateAccountRequestBuilder.A().Named("").Build();
        var result = await _updateValidator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAccount_ValidName_PassesValidation()
    {
        var req = UpdateAccountRequestBuilder.A().Named("New Name").Build();
        var result = await _updateValidator.ValidateAsync(req);
        result.IsValid.Should().BeTrue();
    }
}
