using System.Reflection;
using FlowLedger.Domain.Events;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Events;
using FlowLedger.SharedKernel;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Events;

/// <summary>
/// The outbox stores events as JSON and rebuilds them by type name (#92). These tests cover every concrete event in the Domain
/// assembly by reflection, so a new event that does not survive the round trip (or that the sample factory cannot build) fails here
/// instead of dead-lettering in production.
/// </summary>
public sealed class OutboxSerializerContractTests
{
    private static IEnumerable<Type> EventTypes() => typeof(DomainEventBase).Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDomainEvent).IsAssignableFrom(t))
        .OrderBy(t => t.Name, StringComparer.Ordinal);

    [Fact]
    public void There_is_at_least_one_event_type_to_check()
        => EventTypes().Should().NotBeEmpty();

    [Fact]
    public void Every_domain_event_round_trips_through_the_outbox_serializer_with_its_event_id()
    {
        foreach (var type in EventTypes())
        {
            var e = (IDomainEvent)Build(type);

            var back = OutboxEventSerializer.Deserialize(OutboxEventSerializer.TypeName(e), OutboxEventSerializer.Serialize(e));

            back.Should().BeOfType(type);
            back.Should().Be(e, $"{type.Name}: records compare by value, so every field and the original EventId and OccurredAt must survive");
            back.EventId.Should().Be(e.EventId);
            back.OccurredAt.Should().Be(e.OccurredAt);
        }
    }

    [Fact]
    public void An_unknown_event_type_name_is_rejected_rather_than_resolved_from_the_payload()
    {
        var act = () => OutboxEventSerializer.Deserialize("System.Diagnostics.Process, System", "{}");
        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>Builds an instance with distinct non-default values via its public constructor; throws for a parameter type it does not know, so new shapes are added here deliberately.</summary>
    private static object Build(Type type)
    {
        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters().Select(p => Sample(p.ParameterType, p.Name!)).ToArray();
        return ctor.Invoke(args);
    }

    private static object Sample(Type t, string name)
    {
        if (t == typeof(Guid))
        {
            return Guid.NewGuid();
        }

        if (t == typeof(int))
        {
            return 7 + name.Length;
        }

        if (t == typeof(string))
        {
            return "sample-" + name;
        }

        if (t == typeof(DateOnly))
        {
            return new DateOnly(2031, 5, 17);
        }

        if (t == typeof(bool))
        {
            return true;
        }

        if (t == typeof(Money))
        {
            return new Money(12.34m, "USD");
        }

        if (t.IsEnum)
        {
            var values = Enum.GetValues(t);
            return values.GetValue(values.Length - 1)!;
        }

        var from = t.GetMethod("From", BindingFlags.Public | BindingFlags.Static, [typeof(Guid)]);
        if (from is not null)
        {
            return from.Invoke(null, [Guid.NewGuid()])!;
        }

        throw new NotSupportedException($"Teach OutboxSerializerContractTests.Sample how to build a {t.Name} (parameter '{name}').");
    }
}
