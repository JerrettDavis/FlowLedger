using System.Xml;
using System.Xml.Linq;

namespace FlowLedger.Application.Features.Integrations;

/// <summary>One callable SOAP operation read from a WSDL 1.1 document.</summary>
public sealed record WsdlOperation(
    string Name,
    string SoapAction,
    string SoapVersion,
    string Endpoint,
    string Namespace,
    string BodySkeleton);

internal sealed record WsdlDescription(string? TargetNamespace, IReadOnlyList<WsdlOperation> Operations);

/// <summary>
/// Reads the operations, SOAP actions and endpoints out of a WSDL 1.1 document (SOAP 1.1 and 1.2 bindings). External entities and
/// DTDs are refused, and imports are not followed (an imported WSDL is not fetched), so a hostile document cannot make the server read
/// files or call other hosts.
/// </summary>
internal static class WsdlParser
{
    public const int MaxCharacters = 2_000_000;

    private static readonly XNamespace Wsdl = "http://schemas.xmlsoap.org/wsdl/";
    private static readonly XNamespace Soap11 = "http://schemas.xmlsoap.org/wsdl/soap/";
    private static readonly XNamespace Soap12 = "http://schemas.xmlsoap.org/wsdl/soap12/";

    public static XDocument LoadSafe(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxCharacters,
            MaxCharactersFromEntities = 0,
        };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader);
    }

    /// <exception cref="FormatException">The document is not well-formed XML or not a WSDL 1.1 description.</exception>
    public static WsdlDescription Parse(string xml)
    {
        XDocument doc;
        try
        {
            doc = LoadSafe(xml);
        }
        catch (XmlException ex)
        {
            throw new FormatException("That is not well-formed XML: " + ex.Message, ex);
        }

        var root = doc.Root;
        if (root is null || root.Name != Wsdl + "definitions")
        {
            throw new FormatException("That is not a WSDL 1.1 document (no wsdl:definitions root).");
        }

        var tns = (string?)root.Attribute("targetNamespace");
        var messages = new Dictionary<string, XElement?>(StringComparer.Ordinal);
        foreach (var m in root.Elements(Wsdl + "message"))
        {
            messages[(string?)m.Attribute("name") ?? ""] = m.Elements(Wsdl + "part").FirstOrDefault();
        }

        // portType -> operation name -> input message local name
        var portTypes = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        foreach (var p in root.Elements(Wsdl + "portType"))
        {
            var ops = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var o in p.Elements(Wsdl + "operation"))
            {
                ops[(string?)o.Attribute("name") ?? ""] = Local((string?)o.Element(Wsdl + "input")?.Attribute("message"));
            }

            portTypes[(string?)p.Attribute("name") ?? ""] = ops;
        }

        var operations = new List<WsdlOperation>();
        foreach (var port in root.Elements(Wsdl + "service").Elements(Wsdl + "port"))
        {
            var (version, soapNs) = port.Element(Soap12 + "address") is not null ? ("1.2", Soap12) : ("1.1", Soap11);
            var address = (string?)port.Element(soapNs + "address")?.Attribute("location");
            var bindingName = Local((string?)port.Attribute("binding"));
            var binding = root.Elements(Wsdl + "binding").FirstOrDefault(b => (string?)b.Attribute("name") == bindingName);
            if (address is null || binding is null)
            {
                continue;
            }

            var isRpc = string.Equals((string?)binding.Element(soapNs + "binding")?.Attribute("style"), "rpc", StringComparison.OrdinalIgnoreCase);
            portTypes.TryGetValue(Local((string?)binding.Attribute("type")) ?? "", out var typeOps);
            foreach (var op in binding.Elements(Wsdl + "operation"))
            {
                var name = (string?)op.Attribute("name");
                if (string.IsNullOrEmpty(name) || operations.Any(o => o.Name == name && o.SoapVersion == version))
                {
                    continue;
                }

                var action = (string?)op.Element(soapNs + "operation")?.Attribute("soapAction") ?? "";
                var (ns, skeleton) = Skeleton(name, isRpc, typeOps, messages, tns);
                operations.Add(new WsdlOperation(name, action, version, address, ns, skeleton));
            }
        }

        var ordered = operations.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.SoapVersion, StringComparer.Ordinal).ToList();
        return new WsdlDescription(tns, ordered);
    }

    private static (string Namespace, string Skeleton) Skeleton(
        string operation, bool rpc, Dictionary<string, string?>? typeOps, Dictionary<string, XElement?> messages, string? tns)
    {
        var name = operation;
        var ns = tns ?? "";
        if (!rpc && typeOps is not null && typeOps.TryGetValue(operation, out var msg) && msg is not null
            && messages.TryGetValue(msg, out var part) && part is not null)
        {
            var element = (string?)part.Attribute("element");
            if (element is not null)
            {
                name = Local(element)!;
                var colon = element.IndexOf(':');
                var prefix = colon >= 0 ? element[..colon] : "";
                ns = part.GetNamespaceOfPrefix(prefix)?.NamespaceName ?? tns ?? "";
            }
        }

        var el = string.IsNullOrEmpty(ns) ? new XElement(name) : new XElement(XNamespace.Get(ns) + name);
        return (ns, el.ToString(SaveOptions.DisableFormatting));
    }

    private static string? Local(string? qname)
        => qname is null ? null : qname[(qname.IndexOf(':') + 1)..];
}

/// <summary>Builds a SOAP envelope around a body fragment and detects faults in a response.</summary>
internal static class SoapEnvelope
{
    public const string Ns11 = "http://schemas.xmlsoap.org/soap/envelope/";
    public const string Ns12 = "http://www.w3.org/2003/05/soap-envelope";

    /// <exception cref="FormatException">The body is not a single well-formed XML element.</exception>
    public static (string Xml, string ContentType) Build(WsdlOperation op, string bodyXml)
    {
        XElement body;
        try
        {
            body = WsdlParser.LoadSafe(bodyXml).Root ?? throw new FormatException("The request body is empty.");
        }
        catch (XmlException ex)
        {
            throw new FormatException("The request body is not well-formed XML: " + ex.Message, ex);
        }

        XNamespace ns = op.SoapVersion == "1.2" ? Ns12 : Ns11;
        var envelope = new XElement(ns + "Envelope", new XAttribute(XNamespace.Xmlns + "soap", ns.NamespaceName),
            new XElement(ns + "Body", body));
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + envelope.ToString(SaveOptions.DisableFormatting);
        var contentType = op.SoapVersion == "1.2"
            ? $"application/soap+xml; charset=utf-8; action=\"{op.SoapAction}\""
            : "text/xml; charset=utf-8";
        return (xml, contentType);
    }

    /// <summary>A short fault description when the response is a SOAP fault, otherwise null.</summary>
    public static string? FaultSummary(string responseXml)
    {
        try
        {
            var fault = WsdlParser.LoadSafe(responseXml).Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
            if (fault is null)
            {
                return null;
            }

            var text = fault.Descendants().FirstOrDefault(e => e.Name.LocalName is "faultstring" or "Text")?.Value
                ?? fault.Descendants().FirstOrDefault(e => e.Name.LocalName is "faultcode" or "Value")?.Value
                ?? "SOAP fault";
            text = text.Trim();
            return "SOAP fault: " + (text.Length > 200 ? text[..200] : text);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
