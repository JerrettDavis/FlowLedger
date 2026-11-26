namespace FlowLedger.Contracts;

/// <summary>One distinct raw merchant string and how often it occurs.</summary>
public sealed record MerchantOccurrence(string Raw, int Count);
