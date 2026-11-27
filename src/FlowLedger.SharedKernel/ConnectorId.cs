using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowLedger.SharedKernel;

/// <summary>
/// The stable id of a financial-data connector ("SimpleFIN", "MX", "Quiltt", "Simulated"; see <c>ConnectorIds</c>). One concept that used
/// to travel as <c>ProviderName</c>, <c>ConnectorId</c> and <c>providerName</c> strings (#119). It converts implicitly to and from
/// <see cref="string"/> so persisted values and configuration keep working; comparison ignores case, as the connector registry always has.
/// </summary>
[JsonConverter(typeof(ConnectorIdJsonConverter))]
public readonly record struct ConnectorId
{
    private readonly string? _value;

    /// <summary>Wraps <paramref name="value"/>; a blank id is not a connector.</summary>
    public ConnectorId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    /// <summary>The id text; empty for <c>default</c>.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>True for <c>default(ConnectorId)</c>, which no connector uses.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    public static implicit operator string(ConnectorId id) => id.Value;

    public static implicit operator ConnectorId(string value) => new(value);

    /// <summary>Parses without throwing; blank input yields false.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out ConnectorId id)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            id = default;
            return false;
        }

        id = new ConnectorId(value);
        return true;
    }

    public bool Equals(ConnectorId other) => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    public override string ToString() => Value;
}

/// <summary>Reads and writes a <see cref="ConnectorId"/> as the plain JSON string it replaces.</summary>
public sealed class ConnectorIdJsonConverter : JsonConverter<ConnectorId>
{
    public override ConnectorId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("A connector id must be a string."));

    public override void Write(Utf8JsonWriter writer, ConnectorId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
