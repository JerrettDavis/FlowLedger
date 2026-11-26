using System.Security.Cryptography;
using System.Text;

namespace FlowLedger.SharedKernel;

/// <summary>
/// The one string secret comparison (#100): both sides are hashed first so neither the content nor the length of the
/// secret leaks through timing. Byte-level HMAC comparisons stay in their verifiers.
/// </summary>
public static class ConstantTime
{
    public static bool SecretEquals(string? supplied, string? expected)
    {
        if (string.IsNullOrEmpty(supplied) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }
}
