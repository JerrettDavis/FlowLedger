using FlowLedger.Application.Features.Accounts;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features.Accounts;

public sealed class AccountValidatorTests
{
    private readonly CreateAccountRequestValidator _createValidator = new();
    private readonly UpdateAccountRequestValidator _updateValidator = new();

    [Fact]
    public async Task CreateAccount_ValidRequest_PassesValidation()
    {
        var req = new CreateAccountRequest("Checking", "Checking", 500m, "USD", null, null);
        var result = await _createValidator.ValidateAsync(req);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAccount_EmptyName_FailsValidation()
    {
        var req = new CreateAccountRequest("", "Checking", 100m, "USD", null, null);
        var result = await _createValidator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAccount_InvalidAccountType_FailsValidation()
    {
        var req = new CreateAccountRequest("X", "BadType", 100m, "USD", null, null);
        var result = await _createValidator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAccount_NegativeBalance_FailsValidation()
    {
        var req = new CreateAccountRequest("X", "Checking", -1m, "USD", null, null);
        var result = await _createValidator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAccount_BadCurrencyCode_FailsValidation()
    {
        var req = new CreateAccountRequest("X", "Checking", 100m, "US", null, null);
        var result = await _createValidator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAccount_EmptyName_FailsValidation()
    {
        var req = new UpdateAccountRequest("");
        var result = await _updateValidator.ValidateAsync(req);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAccount_ValidName_PassesValidation()
    {
        var req = new UpdateAccountRequest("New Name");
        var result = await _updateValidator.ValidateAsync(req);
        result.IsValid.Should().BeTrue();
    }
}
