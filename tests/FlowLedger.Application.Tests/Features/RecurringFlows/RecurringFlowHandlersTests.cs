using FlowLedger.Application.Features.RecurringFlows;
using FlowLedger.Application.Tests.Fakes;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features.RecurringFlows;

public sealed class RecurringFlowHandlersTests
{
    private readonly FakeRecurringFlowRepository _repo = new();
    private readonly FakeTenantContext _tenant = new();

    private CreateRecurringFlowRequest MonthlyRentRequest(Guid? accountId = null) => new(
        accountId ?? Guid.NewGuid(),
        "Rent",
        1500m,
        "USD",
        "Debit",
        "Fixed",
        "Monthly",
        1,    // DayOfMonth
        null, // SecondDayOfMonth
        null, // IntervalWeeks
        null, // AnchorDayOfWeek
        new DateOnly(2026, 1, 1),
        null,
        null,
        "Landlord");

    [Fact]
    public async Task CreateRecurringFlow_Monthly_StoresAndReturnsDto()
    {
        var handler = new CreateRecurringFlowHandler(_repo, _tenant);
        var dto = await handler.HandleAsync(MonthlyRentRequest());

        dto.Name.Should().Be("Rent");
        dto.Amount.Should().Be(1500m);
        dto.RecurrenceFrequency.Should().Be("Monthly");
        dto.DayOfMonth.Should().Be(1);
        dto.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task CreateRecurringFlow_Weekly_StoresPattern()
    {
        var handler = new CreateRecurringFlowHandler(_repo, _tenant);
        var req = MonthlyRentRequest() with
        {
            RecurrenceFrequency = "Weekly",
            AnchorDayOfWeek = "Friday",
            DayOfMonth = null
        };
        var dto = await handler.HandleAsync(req);

        dto.RecurrenceFrequency.Should().Be("Weekly");
        dto.AnchorDayOfWeek.Should().Be("Friday");
    }

    [Fact]
    public async Task GetRecurringFlow_ExistingId_ReturnsDto()
    {
        var createHandler = new CreateRecurringFlowHandler(_repo, _tenant);
        var created = await createHandler.HandleAsync(MonthlyRentRequest());

        var getHandler = new GetRecurringFlowHandler(_repo);
        var dto = await getHandler.HandleAsync(created.Id);

        dto.Should().NotBeNull();
        dto!.Name.Should().Be("Rent");
    }

    [Fact]
    public async Task UpdateRecurringFlow_ChangesAmount()
    {
        var createHandler = new CreateRecurringFlowHandler(_repo, _tenant);
        var created = await createHandler.HandleAsync(MonthlyRentRequest());

        var updateHandler = new UpdateRecurringFlowHandler(_repo);
        var updated = await updateHandler.HandleAsync(created.Id, new UpdateRecurringFlowRequest(1600m, "Fixed"));

        updated.Should().NotBeNull();
        updated!.Amount.Should().Be(1600m);
    }

    [Fact]
    public async Task DeactivateRecurringFlow_RemovesFromActiveList()
    {
        var createHandler = new CreateRecurringFlowHandler(_repo, _tenant);
        var created = await createHandler.HandleAsync(MonthlyRentRequest());

        var deactivateHandler = new DeactivateRecurringFlowHandler(_repo);
        var found = await deactivateHandler.HandleAsync(created.Id);

        found.Should().BeTrue();

        var listHandler = new ListRecurringFlowsHandler(_repo);
        var flows = await listHandler.HandleAsync();
        flows.Should().BeEmpty();
    }
}
