using System.Text.Json;
using FlowLedger.SharedKernel;
using FluentAssertions;

namespace FlowLedger.Application.Tests;

public sealed class ConnectorIdTests
{
    [Fact]
    public void Converts_implicitly_to_and_from_the_persisted_string()
    {
        ConnectorId id = "SimpleFIN";
        string text = id;

        text.Should().Be("SimpleFIN");
        id.ToString().Should().Be("SimpleFIN");
    }

    [Fact]
    public void Compares_without_regard_to_case_like_the_connector_registry()
    {
        ConnectorId upper = "MX";
        ConnectorId lower = "mx";

        (upper == lower).Should().BeTrue();
        upper.GetHashCode().Should().Be(lower.GetHashCode());
        new HashSet<ConnectorId> { upper }.Contains(lower).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_id_is_not_a_connector(string blank)
    {
        var act = () => new ConnectorId(blank);

        act.Should().Throw<ArgumentException>();
        ConnectorId.TryParse(blank, out _).Should().BeFalse();
        ConnectorId.TryParse(null, out _).Should().BeFalse();
    }

    [Fact]
    public void Serializes_as_a_plain_json_string()
    {
        var json = JsonSerializer.Serialize(new Holder("Quiltt"));

        json.Should().Be("""{"Connector":"Quiltt"}""");
        JsonSerializer.Deserialize<Holder>(json)!.Connector.Should().Be((ConnectorId)"Quiltt");
    }

    private sealed record Holder(ConnectorId Connector);
}
