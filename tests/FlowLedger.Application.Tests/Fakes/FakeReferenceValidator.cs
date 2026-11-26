using FlowLedger.Application.Abstractions;

namespace FlowLedger.Application.Tests.Fakes;

/// <summary>Accepts every reference (handler unit tests); ownership is covered by ReferenceValidator tests and the API tests.</summary>
public sealed class FakeReferenceValidator : IReferenceValidator
{
    public Task EnsureAccountAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;

    public Task EnsureCategoryAsync(Guid? categoryId, CancellationToken ct = default) => Task.CompletedTask;
}
