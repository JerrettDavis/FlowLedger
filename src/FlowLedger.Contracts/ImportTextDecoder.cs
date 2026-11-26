using System.Text;

namespace FlowLedger.Contracts;

/// <summary>
/// Decodes an uploaded bank file to text (#128). Browsers and <c>StreamReader</c> assume UTF-8 and silently turn every non-UTF-8 byte
/// into U+FFFD, which wrecks accented merchant names in the Windows-1252 exports many banks still produce (and changes fingerprints
/// between re-imports). Order: a BOM wins; then strict UTF-8; then, for a file that is not valid UTF-8, Windows-1252
/// (an OFX header that declares <c>CHARSET:1252</c> goes straight to it).
/// </summary>
public static class ImportTextDecoder
{
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        if (DeclaresCp1252(bytes))
        {
            return Cp1252().GetString(bytes);
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Cp1252().GetString(bytes);
        }
    }

    private static bool DeclaresCp1252(ReadOnlySpan<byte> bytes)
    {
        // OFX 1.x is SGML with a plain-ASCII header block ("CHARSET:1252"); only the first lines matter.
        var head = Encoding.ASCII.GetString(bytes[..Math.Min(bytes.Length, 512)]);
        return head.StartsWith("OFXHEADER", StringComparison.OrdinalIgnoreCase)
            && head.Contains("CHARSET:1252", StringComparison.OrdinalIgnoreCase);
    }

    private static Encoding Cp1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }
}
