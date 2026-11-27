using System.Text;

namespace FlowLedger.Application.Features.Integrations;

/// <summary>SOAP/WSDL calls.</summary>
public sealed partial class IntegrationService
{
    // ── SOAP ─────────────────────────────────────────────────────────────────────

    /// <summary>Fetches the WSDL and lists what can be called.</summary>
    public async Task<SoapOperationsResult> SoapOperationsAsync(Guid id, CancellationToken ct = default)
    {
        var (integration, wsdl, error) = await LoadWsdlAsync(id, ct);
        return integration is null || wsdl is null
            ? new SoapOperationsResult([], error)
            : new SoapOperationsResult(wsdl.Operations, wsdl.Operations.Count == 0 ? "The WSDL has no SOAP operations." : null);
    }

    public async Task<(IntegrationCallResult? Result, string? Error)> SoapInvokeAsync(Guid id, SoapInvokeRequest r, CancellationToken ct = default)
    {
        var (integration, wsdl, error) = await LoadWsdlAsync(id, ct);
        if (integration is null || wsdl is null)
        {
            return (null, error);
        }

        var candidates = wsdl.Operations.Where(o => o.Name.Equals(r.Operation, StringComparison.Ordinal)).ToList();
        var op = candidates.FirstOrDefault(o => r.Version is null || o.SoapVersion == r.Version) ?? candidates.FirstOrDefault();
        if (op is null)
        {
            return (null, $"The WSDL has no operation named '{r.Operation}'.");
        }

        if (r.BodyXml is null || Encoding.UTF8.GetByteCount(r.BodyXml) > MaxRequestBytes)
        {
            return (null, "Provide the request body XML (256 KB maximum).");
        }

        string envelope;
        string contentType;
        try
        {
            (envelope, contentType) = SoapEnvelope.Build(op, r.BodyXml);
        }
        catch (FormatException ex)
        {
            return (null, ex.Message);
        }

        var endpoint = integration.Config.SoapEndpoint ?? op.Endpoint;
        if (!IntegrationUrl.TryValidate(endpoint, AllowLocal, out var checkedEndpoint, out var endpointError))
        {
            return (null, "The service endpoint in the WSDL isn't allowed: " + endpointError);
        }

        var secrets = await store.GetSecretsAsync(TenantId, id, ct);
        var headers = OutboundHeaders.Build(integration.Config, secrets, new Dictionary<string, string>());
        if (op.SoapVersion != "1.2")
        {
            headers.Add(new KeyValuePair<string, string>("SOAPAction", "\"" + op.SoapAction.Replace("\"", "") + "\""));
        }

        var result = await SendAndLogAsync(
            integration, "soap:" + op.Name, new TransportRequest("POST", checkedEndpoint!, headers, envelope, contentType), ct);
        var fault = SoapEnvelope.FaultSummary(result.Body);
        return (fault is null ? result : result with { Error = fault }, null);
    }

    private async Task<(Integration? Integration, WsdlDescription? Wsdl, string? Error)> LoadWsdlAsync(Guid id, CancellationToken ct)
    {
        var integration = await store.GetAsync(TenantId, id, ct);
        if (integration is null || integration.Kind != IntegrationKind.Soap)
        {
            return (null, null, "That SOAP integration doesn't exist.");
        }

        if (!integration.Enabled)
        {
            return (null, null, "That integration is turned off.");
        }

        var secrets = await store.GetSecretsAsync(TenantId, id, ct);
        var headers = OutboundHeaders.Build(integration.Config, secrets, new Dictionary<string, string>());
        var response = await SendAndLogAsync(integration, "wsdl", new TransportRequest("GET", integration.Config.Url, headers, null, null), ct);
        if (response.Error is not null)
        {
            return (integration, null, response.Error);
        }

        try
        {
            return (integration, WsdlParser.Parse(response.Body), null);
        }
        catch (FormatException ex)
        {
            return (integration, null, ex.Message);
        }
    }
}
