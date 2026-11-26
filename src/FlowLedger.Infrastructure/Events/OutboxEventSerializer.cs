using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowLedger.Domain.Events;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.SharedKernel;

namespace FlowLedger.Infrastructure.Events;

/// <summary>Round-trips domain events through the outbox. Types are resolved by name from the Domain events assembly only (never from payload-supplied assembly names).</summary>
internal static class OutboxEventSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new MoneyConverter() } };

    private static readonly ConcurrentDictionary<string, Type> KnownTypes = new(
        typeof(DomainEventBase).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDomainEvent).IsAssignableFrom(t))
            .ToDictionary(t => t.Name, t => t));

    public static string TypeName(IDomainEvent e) => e.GetType().Name;

    public static string Serialize(IDomainEvent e) => JsonSerializer.Serialize(e, e.GetType(), Options);

    public static IDomainEvent Deserialize(string typeName, string payload)
    {
        if (!KnownTypes.TryGetValue(typeName, out var type))
        {
            throw new InvalidOperationException($"Unknown outbox event type '{typeName}'.");
        }

        return (IDomainEvent?)JsonSerializer.Deserialize(payload, type, Options)
            ?? throw new InvalidOperationException($"Outbox payload for '{typeName}' deserialised to null.");
    }

    /// <summary>Money has two public constructors, so it needs an explicit shape: <c>{ "amount": 1.25, "currency": "USD" }</c>.</summary>
    private sealed class MoneyConverter : JsonConverter<Money>
    {
        public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            return new Money(root.GetProperty("amount").GetDecimal(), root.GetProperty("currency").GetString()!);
        }

        public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("amount", value.Amount);
            writer.WriteString("currency", value.Currency.Code);
            writer.WriteEndObject();
        }
    }
}
