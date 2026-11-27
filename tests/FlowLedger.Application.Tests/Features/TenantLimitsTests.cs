using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Query;
using FlowLedger.Application.Tests.Fakes;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features;

/// <summary>#166: per-tenant caps on collections that previously had none.</summary>
public sealed class TenantLimitsTests
{
    private sealed class MemoryReportStore : ISavedReportStore
    {
        private readonly List<SavedReportDto> _items = [];

        public Task<IReadOnlyList<SavedReportDto>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SavedReportDto>>(_items.ToList());

        public Task<SavedReportDto?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_items.FirstOrDefault(i => i.Id == id.ToString()));

        public Task<SavedReportDto> CreateAsync(SaveReportRequest request, CancellationToken ct = default)
        {
            var dto = SavedReportDtoBuilder.A().WithId(Guid.NewGuid().ToString()).Named(request.Name).WithDescription(request.Description).OfKind(request.Kind).WithPanels([.. request.Panels]).CreatedAt(null).Build();
            _items.Add(dto);
            return Task.FromResult(dto);
        }

        public Task<SavedReportDto?> UpdateAsync(Guid id, SaveReportRequest request, CancellationToken ct = default) => Task.FromResult<SavedReportDto?>(null);

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => Task.FromResult(false);
    }

    private static SaveReportRequest Report(string name) =>
        new(name, null, "View", [ReportPanelBuilder.A()
                                             .WithQuery("transactions | take 1")
                                             .WithViz(VizSpecBuilder.A().WithKind("table").Build())
                                             .Build()]);

    [Fact]
    public void EnsureRoom_allows_up_to_the_limit_and_refuses_beyond_it()
    {
        TenantLimits.EnsureRoom(9, 1, 10, "things");

        var act = () => TenantLimits.EnsureRoom(10, 1, 10, "things");

        act.Should().Throw<TenantQuotaExceededException>().Which.Limit.Should().Be(10);
    }

    [Fact]
    public async Task Saving_a_report_beyond_the_limit_is_refused_and_does_not_store_it()
    {
        var store = new MemoryReportStore();
        var handler = new SaveReportHandler(store);
        for (var i = 0; i < TenantLimits.MaxSavedReports; i++)
        {
            await handler.CreateAsync(Report("r" + i));
        }

        var act = () => handler.CreateAsync(Report("one too many"));

        await act.Should().ThrowAsync<TenantQuotaExceededException>();
        (await store.ListAsync()).Should().HaveCount(TenantLimits.MaxSavedReports);
    }

    [Fact]
    public async Task Creating_an_account_beyond_the_limit_is_refused_even_when_older_ones_are_deactivated()
    {
        var repo = new FakeAccountRepository();
        var handler = new CreateAccountHandler(repo, new FakeTenantContext(), FlowLedger.TestSupport.TestClock.Provider);
        for (var i = 0; i < TenantLimits.MaxAccounts; i++)
        {
            var created = await handler.HandleAsync(CreateAccountRequestBuilder.A().Named("A" + i).OfType("Checking").StartingAt(0m).Build());
            (await repo.GetByIdAsync(FlowLedger.Domain.ValueObjects.AccountId.From(created.Id)))!.Deactivate();
        }

        var act = () => handler.HandleAsync(CreateAccountRequestBuilder.A().Named("overflow").OfType("Checking").StartingAt(0m).Build());

        await act.Should().ThrowAsync<TenantQuotaExceededException>("inactive accounts still occupy storage");
    }

    [Fact]
    public void Saved_report_payload_over_the_character_cap_fails_validation()
    {
        var huge = SaveReportRequestBuilder.A().Named("big")
            .WithPanels(ReportPanelBuilder.A()
                                        .WithQuery("transactions")
                                        .WithViz(VizSpecBuilder.A()
                                                                             .WithKind("table")
                                                                             .WithY(Enumerable.Range(0, 20_000).Select(i => "column" + i).ToList())
                                                                             .Build())
                                        .Build()).Build();

        var result = new SaveReportRequestValidator().Validate(huge);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("may not exceed"));
    }
}
