using FlowLedger.Web.ApiClient;

namespace FlowLedger.Web.Tests.Builders;

/// <summary>Builds <see cref="SetAccountConnectorRequest"/>: the MX connector with provider account "acct-1".</summary>
public sealed class SetAccountConnectorRequestBuilder
{
    private string _connectorId = "MX";
    private string? _providerAccountId = "acct-1";

    public static SetAccountConnectorRequestBuilder A() => new();

    public SetAccountConnectorRequestBuilder OnConnector(string connectorId) { _connectorId = connectorId; return this; }
    public SetAccountConnectorRequestBuilder WithProviderAccount(string? providerAccountId) { _providerAccountId = providerAccountId; return this; }

    public SetAccountConnectorRequest Build() => new(_connectorId, _providerAccountId);

    public static implicit operator SetAccountConnectorRequest(SetAccountConnectorRequestBuilder builder) => builder.Build();
}
