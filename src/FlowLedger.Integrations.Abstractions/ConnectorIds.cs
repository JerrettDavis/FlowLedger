namespace FlowLedger.Integrations.Abstractions;

/// <summary>
/// The stable connector ids (equal to each provider's <see cref="IFinancialDataProvider.ProviderName"/>). One definition, so
/// host and sync code never compare against bare strings (#89).
/// </summary>
public static class ConnectorIds
{
    public const string Simulated = "Simulated";
    public const string SimpleFin = "SimpleFIN";
    public const string Mx = "MX";
    public const string Quiltt = "Quiltt";
}
