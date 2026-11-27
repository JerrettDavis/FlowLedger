using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FlowLedger.Infrastructure.Persistence;

/// <summary>Stores a <see cref="ConnectorId"/> as the plain text column it replaces; a blank column reads back as <c>default</c> instead of throwing.</summary>
internal sealed class ConnectorIdConverter()
    : ValueConverter<ConnectorId, string>(id => id.Value, text => string.IsNullOrWhiteSpace(text) ? default : new ConnectorId(text))
{
    public static readonly ConnectorIdConverter Instance = new();
}
