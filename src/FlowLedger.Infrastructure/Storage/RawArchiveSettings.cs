using FlowLedger.Infrastructure.DataSafety;
using Microsoft.Extensions.Configuration;

namespace FlowLedger.Infrastructure.Storage;

/// <summary>
/// Raw provider payload archive settings (#142). Both features are OPT-IN and off by default (ADR 0007: the archive is the only copy of
/// billable provider data, so nothing is sealed or deleted unless the operator asks):
/// <list type="bullet">
/// <item><c>Storage:EncryptionKey</c> (base64, 32 bytes) seals new objects as <c>FLRAWv2:</c> + a SecretBox v2 envelope bound to tenant and
/// provider. Existing plaintext objects stay readable (the header tells them apart). <c>Storage:EncryptionKeyPrevious</c> keeps older keys readable.</item>
/// <item><c>Storage:RawArchiveRetentionDays</c> (default 0 = keep forever) lets the scheduled backup job delete archive objects older than that.</item>
/// </list>
/// </summary>
internal sealed record RawArchiveSettings(SecretKeyRing Ring, int RetentionDays)
{
    public const string UnattributedSegment = "_unattributed";

    public static RawArchiveSettings Default { get; } = new(new SecretKeyRing(null), 0);

    public bool Encrypts => Ring.CanSeal;

    public static RawArchiveSettings From(IConfiguration configuration)
    {
        var days = configuration.GetValue<int?>("Storage:RawArchiveRetentionDays");
        return new RawArchiveSettings(
            SecretKeyRing.From(configuration["Storage:EncryptionKey"], configuration["Storage:EncryptionKeyPrevious"]),
            days is > 0 ? days.Value : 0);
    }
}
