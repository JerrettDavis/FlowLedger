using System.Text;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Imports;

/// <summary>Charset handling for uploaded bank files (#128): UTF-8 first, Windows-1252 when the bytes are not valid UTF-8.</summary>
public sealed class ImportTextDecoderTests
{
    private static Encoding Cp1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    [Fact]
    public void Utf8_is_decoded_as_utf8()
        => ImportTextDecoder.Decode(Encoding.UTF8.GetBytes("Café Zoë")).Should().Be("Café Zoë");

    [Fact]
    public void Utf8_with_a_bom_drops_the_bom()
        => ImportTextDecoder.Decode(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("Café")).ToArray()).Should().Be("Café");

    [Fact]
    public void Windows_1252_bytes_are_not_turned_into_replacement_characters()
    {
        var text = ImportTextDecoder.Decode(Cp1252().GetBytes("Café Müller – 5€"));
        text.Should().Be("Café Müller – 5€").And.NotContain("�");
    }

    [Fact]
    public void An_ofx_header_declaring_charset_1252_is_decoded_as_1252()
    {
        var bytes = Cp1252().GetBytes("OFXHEADER:100\nCHARSET:1252\n<OFX><NAME>Zoë</NAME></OFX>");
        ImportTextDecoder.Decode(bytes).Should().Contain("Zoë");
    }

    [Fact]
    public void A_cp1252_file_imports_with_accents_intact()
    {
        var csv = ImportTextDecoder.Decode(Cp1252().GetBytes("Date,Amount,Description\n2024-01-10,-4.50,Café Müller\n"));
        csv.Should().Contain("Café Müller");
    }
}
