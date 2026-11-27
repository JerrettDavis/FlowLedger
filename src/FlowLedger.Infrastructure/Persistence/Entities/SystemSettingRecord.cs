namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>A host-wide key/value setting (the data-environment marker lives here) (#154). No tenant column, no filter; reached only through <see cref="HostDb"/>.</summary>
internal sealed class SystemSettingRecord
{
    public string Key { get; private set; } = string.Empty;
    public string Value { get; private set; } = string.Empty;
    public DateTimeOffset UpdatedUtc { get; private set; }

    private SystemSettingRecord() { }
}
