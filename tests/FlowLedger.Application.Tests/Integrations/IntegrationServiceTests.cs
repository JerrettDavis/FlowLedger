using System.Text;
using FlowLedger.Application.Features.HomeAssistant;
using FlowLedger.Application.Features.Integrations;
using FlowLedger.Application.Tests.Fakes;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Integrations;

public sealed class IntegrationServiceTests
{
    private const string Wsdl = """
        <?xml version="1.0"?>
        <wsdl:definitions xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/" xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
            xmlns:tns="http://example.com/calc" xmlns:xsd="http://www.w3.org/2001/XMLSchema" targetNamespace="http://example.com/calc">
          <wsdl:message name="AddIn"><wsdl:part name="parameters" element="tns:Add"/></wsdl:message>
          <wsdl:message name="AddOut"><wsdl:part name="parameters" element="tns:AddResponse"/></wsdl:message>
          <wsdl:portType name="CalcPort">
            <wsdl:operation name="Add"><wsdl:input message="tns:AddIn"/><wsdl:output message="tns:AddOut"/></wsdl:operation>
          </wsdl:portType>
          <wsdl:binding name="CalcBinding" type="tns:CalcPort">
            <soap:binding style="document" transport="http://schemas.xmlsoap.org/soap/http"/>
            <wsdl:operation name="Add"><soap:operation soapAction="http://example.com/calc/Add"/></wsdl:operation>
          </wsdl:binding>
          <wsdl:service name="Calc"><wsdl:port name="CalcPort" binding="tns:CalcBinding"><soap:address location="https://calc.example.com/svc"/></wsdl:port></wsdl:service>
        </wsdl:definitions>
        """;

    private sealed class FakeStore : IIntegrationStore
    {
        public bool CanStoreSecrets { get; set; } = true;
        public Dictionary<Guid, Integration> Items { get; } = [];
        public Dictionary<Guid, IntegrationSecrets> Secrets { get; } = [];
        public List<IntegrationDelivery> Log { get; } = [];

        public Task<IReadOnlyList<Integration>> ListAsync(Guid tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Integration>>([.. Items.Values]);

        public Task<Integration?> GetAsync(Guid tenantId, Guid id, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(id));

        public Task<IntegrationSecrets> GetSecretsAsync(Guid tenantId, Guid id, CancellationToken ct = default)
            => Task.FromResult(Secrets.GetValueOrDefault(id) ?? new IntegrationSecrets(null, null));

        public Task SaveAsync(Guid tenantId, Integration integration, string? credential, bool clearCredential, string? signingSecret, CancellationToken ct = default)
        {
            var current = Secrets.GetValueOrDefault(integration.Id) ?? new IntegrationSecrets(null, null);
            Items[integration.Id] = integration;
            Secrets[integration.Id] = new IntegrationSecrets(clearCredential ? null : credential ?? current.Credential, signingSecret ?? current.SigningSecret);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default) => Task.FromResult(Items.Remove(id));

        public Task RecordDeliveryAsync(Guid tenantId, IntegrationDelivery delivery, CancellationToken ct = default)
        {
            Log.Add(delivery);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<IntegrationDelivery>> ListDeliveriesAsync(Guid tenantId, Guid id, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IntegrationDelivery>>([.. Log.Where(d => d.IntegrationId == id)]);
    }

    private sealed class FakeTransport : IIntegrationTransport
    {
        public List<TransportRequest> Sent { get; } = [];
        public Func<TransportRequest, TransportResponse> Respond { get; set; } = _ => new TransportResponse(200, "application/json", "{}", false, null);

        public Task<TransportResponse> SendAsync(TransportRequest request, bool allowLocalNetwork, CancellationToken ct = default)
        {
            Sent.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class Network(bool local) : IHomeAssistantNetworkPolicy
    {
        public bool AllowLocalNetwork(Guid tenantId) => local;
    }

    private static (IntegrationService Service, FakeStore Store, FakeTransport Transport) Create(bool local = false)
    {
        var store = new FakeStore();
        var transport = new FakeTransport();
        return (new IntegrationService(store, transport, new Network(local), new FakeTenantContext(), FlowLedger.TestSupport.TestClock.Provider), store, transport);
    }

    private static IntegrationSaveRequest Rest(string url = "https://api.example.com/v1", IntegrationAuth auth = IntegrationAuth.None, string? credential = null, string? user = null)
        => new("Example API", IntegrationKind.Rest, true, url, auth, user, credential, false, null, null, null);

    private static IntegrationSaveRequest Webhook(string[]? events = null)
        => new("Hook", IntegrationKind.Webhook, true, "https://hooks.example.com/in", IntegrationAuth.None, null, null, false, null, events, null);

    // ── definitions ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://api.example.com", "https")]
    [InlineData("https://169.254.169.254/latest", "not allowed")]
    [InlineData("https://192.168.1.10/x", "household owner")]
    [InlineData("https://localhost/x", "household owner")]
    [InlineData("ftp://api.example.com", "http")]
    [InlineData("https://user:pw@api.example.com", "credentials")]
    public async Task Rejects_unsafe_urls_for_ordinary_tenants(string url, string reason)
    {
        var (service, _, _) = Create();
        var result = await service.SaveAsync(null, Rest(url));
        result.Integration.Should().BeNull();
        result.Errors!["url"][0].Should().Contain(reason);
    }

    [Fact]
    public async Task Household_owner_may_use_a_local_network_address_but_never_link_local()
    {
        var (service, _, _) = Create(local: true);
        (await service.SaveAsync(null, Rest("http://192.168.1.10:8123/api"))).Integration.Should().NotBeNull();
        (await service.SaveAsync(null, Rest("http://169.254.169.254/latest"))).Integration.Should().BeNull();
    }

    [Fact]
    public async Task Credentials_are_stored_but_never_appear_in_the_read_model()
    {
        var (service, store, _) = Create();
        var saved = await service.SaveAsync(null, Rest(auth: IntegrationAuth.Bearer, credential: "tok-SECRET"));
        saved.Integration!.HasCredential.Should().BeTrue();
        System.Text.Json.JsonSerializer.Serialize(saved.Integration).Should().NotContain("tok-SECRET");
        store.Secrets[saved.Integration.Id].Credential.Should().Be("tok-SECRET");

        // Update without a credential keeps it.
        var updated = await service.SaveAsync(saved.Integration.Id, Rest(auth: IntegrationAuth.Bearer) with { Name = "Renamed" });
        updated.Integration!.HasCredential.Should().BeTrue();
        store.Secrets[saved.Integration.Id].Credential.Should().Be("tok-SECRET");
    }

    [Fact]
    public async Task Refuses_secrets_when_the_server_has_no_encryption_key()
    {
        var (service, store, _) = Create();
        store.CanStoreSecrets = false;
        (await service.SaveAsync(null, Rest(auth: IntegrationAuth.Bearer, credential: "x"))).Errors.Should().ContainKey("credential");
        (await service.SaveAsync(null, Webhook())).Errors.Should().ContainKey("credential");
        (await service.SaveAsync(null, Rest())).Integration.Should().NotBeNull();
    }

    [Fact]
    public async Task Authentication_requires_a_credential_and_reserved_headers_are_refused()
    {
        var (service, _, _) = Create();
        (await service.SaveAsync(null, Rest(auth: IntegrationAuth.Bearer))).Errors.Should().ContainKey("credential");
        (await service.SaveAsync(null, Rest(auth: IntegrationAuth.Basic, credential: "pw"))).Errors.Should().ContainKey("authUser");
        (await service.SaveAsync(null, Rest(auth: IntegrationAuth.ApiKey, credential: "k", user: "Authorization"))).Errors.Should().ContainKey("authUser");
        var headers = Rest() with { Headers = new() { ["Authorization"] = "Bearer stolen" } };
        (await service.SaveAsync(null, headers)).Errors.Should().ContainKey("headers");
    }

    [Fact]
    public async Task A_webhook_gets_a_signing_secret_once_and_can_rotate_it()
    {
        var (service, store, _) = Create();
        var saved = await service.SaveAsync(null, Webhook());
        saved.NewSigningSecret.Should().StartWith("whsec_");
        saved.Integration!.HasSigningSecret.Should().BeTrue();

        var again = await service.SaveAsync(saved.Integration.Id, Webhook());
        again.NewSigningSecret.Should().BeNull();
        store.Secrets[saved.Integration.Id].SigningSecret.Should().Be(saved.NewSigningSecret);

        var (_, secret, _) = await service.RotateSigningSecretAsync(saved.Integration.Id);
        secret.Should().NotBe(saved.NewSigningSecret);
        store.Secrets[saved.Integration.Id].SigningSecret.Should().Be(secret);
    }

    [Fact]
    public async Task The_kind_of_an_integration_cannot_change()
    {
        var (service, _, _) = Create();
        var saved = await service.SaveAsync(null, Rest());
        (await service.SaveAsync(saved.Integration!.Id, Webhook())).Errors.Should().ContainKey("kind");
    }

    // ── REST ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rest_call_joins_path_adds_authentication_and_logs_without_the_query()
    {
        var (service, store, transport) = Create();
        var saved = await service.SaveAsync(null, Rest("https://api.example.com/v1", IntegrationAuth.Bearer, "tok"));
        var (result, error) = await service.CallRestAsync(saved.Integration!.Id, new RestCallRequest("get", "/items", "page=2&token=zzz", null, null, null));

        error.Should().BeNull();
        result!.StatusCode.Should().Be(200);
        var sent = transport.Sent.Single();
        sent.Url.Should().Be("https://api.example.com/v1/items?page=2&token=zzz");
        sent.Method.Should().Be("GET");
        sent.Headers.Should().Contain(h => h.Key == "Authorization" && h.Value == "Bearer tok");
        store.Log.Single().Target.Should().Be("https://api.example.com/v1/items");
        store.Log.Single().Target.Should().NotContain("zzz");
    }

    [Theory]
    [InlineData("https://evil.example.com/x")]
    [InlineData("//evil.example.com/x")]
    [InlineData("/a/../../b")]
    public async Task Rest_call_cannot_leave_the_base_url(string path)
    {
        var (service, _, transport) = Create();
        var saved = await service.SaveAsync(null, Rest());
        var (result, error) = await service.CallRestAsync(saved.Integration!.Id, new RestCallRequest("GET", path, null, null, null, null));
        result.Should().BeNull();
        error.Should().NotBeNull();
        transport.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Rest_http_errors_are_reported_and_logged()
    {
        var (service, store, transport) = Create();
        transport.Respond = _ => new TransportResponse(503, "text/plain", "down", false, null);
        var saved = await service.SaveAsync(null, Rest());
        var (result, _) = await service.CallRestAsync(saved.Integration!.Id, new RestCallRequest("GET", "/x", null, null, null, null));
        result!.Error.Should().Contain("503");
        store.Log.Single().StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task A_disabled_integration_does_not_send()
    {
        var (service, _, transport) = Create();
        var saved = await service.SaveAsync(null, Rest() with { Enabled = false });
        var (result, error) = await service.CallRestAsync(saved.Integration!.Id, new RestCallRequest("GET", "/x", null, null, null, null));
        result.Should().BeNull();
        error.Should().Contain("off");
        transport.Sent.Should().BeEmpty();
    }

    // ── webhooks ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Signature_is_hmac_sha256_over_timestamp_dot_body()
    {
        // Independently computed: HMAC-SHA256(key="k", msg="1700000000.{\"a\":1}")
        var expected = "sha256=" + Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("k"), Encoding.UTF8.GetBytes("1700000000.{\"a\":1}")));
        IntegrationService.Sign("k", "1700000000", "{\"a\":1}").Should().Be(expected);
    }

    [Fact]
    public async Task Dispatch_signs_and_sends_only_to_subscribed_enabled_webhooks()
    {
        var (service, store, transport) = Create();
        var hook = await service.SaveAsync(null, Webhook([IntegrationEvents.ImportCompleted]));
        await service.SaveAsync(null, Webhook([IntegrationEvents.SyncCompleted]) with { Name = "Other" });
        await service.SaveAsync(null, Webhook([IntegrationEvents.ImportCompleted]) with { Name = "Off", Enabled = false });

        var sent = await service.DispatchAsync(IntegrationEvents.ImportCompleted, new { imported = 3 });

        sent.Should().Be(1);
        var request = transport.Sent.Single();
        request.Method.Should().Be("POST");
        request.Body.Should().Contain("\"event\":\"import.completed\"").And.Contain("\"imported\":3");
        var timestamp = request.Headers.Single(h => h.Key == IntegrationService.TimestampHeader).Value;
        var signature = request.Headers.Single(h => h.Key == IntegrationService.SignatureHeader).Value;
        signature.Should().Be(IntegrationService.Sign(hook.NewSigningSecret!, timestamp, request.Body!));
        store.Log.Single().Trigger.Should().Be("event:import.completed");
    }

    [Fact]
    public async Task Dispatch_never_throws_when_the_receiver_is_down()
    {
        var (service, store, transport) = Create();
        transport.Respond = _ => new TransportResponse(null, null, "", false, "Could not reach the service.");
        await service.SaveAsync(null, Webhook());
        var act = () => service.DispatchAsync(IntegrationEvents.ImportCompleted, null);
        await act.Should().NotThrowAsync();
        store.Log.Single().Error.Should().Be("Could not reach the service.");
    }

    // ── SOAP ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Wsdl_parser_reads_operations_actions_endpoint_and_a_body_skeleton()
    {
        var op = WsdlParser.Parse(Wsdl).Operations.Single();
        op.Name.Should().Be("Add");
        op.SoapAction.Should().Be("http://example.com/calc/Add");
        op.SoapVersion.Should().Be("1.1");
        op.Endpoint.Should().Be("https://calc.example.com/svc");
        op.BodySkeleton.Should().Be("<Add xmlns=\"http://example.com/calc\" />");
    }

    [Fact]
    public void Wsdl_parser_refuses_dtds_and_non_wsdl()
    {
        var evil = "<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><d>&x;</d>";
        var act = () => WsdlParser.Parse(evil);
        act.Should().Throw<FormatException>();
        var notWsdl = () => WsdlParser.Parse("<a/>");
        notWsdl.Should().Throw<FormatException>().WithMessage("*WSDL*");
    }

    [Fact]
    public async Task Soap_invoke_wraps_the_body_in_an_envelope_with_soapaction()
    {
        var (service, _, transport) = Create();
        transport.Respond = r => r.Method == "GET"
            ? new TransportResponse(200, "text/xml", Wsdl, false, null)
            : new TransportResponse(200, "text/xml", "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><AddResponse/></s:Body></s:Envelope>", false, null);
        var saved = await service.SaveAsync(null, new IntegrationSaveRequest("Calc", IntegrationKind.Soap, true, "https://calc.example.com/svc?wsdl", IntegrationAuth.None, null, null, false, null, null, null));

        var ops = await service.SoapOperationsAsync(saved.Integration!.Id);
        ops.Operations.Should().ContainSingle(o => o.Name == "Add");

        var (result, error) = await service.SoapInvokeAsync(saved.Integration.Id, new SoapInvokeRequest("Add", "<Add xmlns=\"http://example.com/calc\"><a>1</a></Add>"));
        error.Should().BeNull();
        result!.Error.Should().BeNull();
        var post = transport.Sent.Last();
        post.Url.Should().Be("https://calc.example.com/svc");
        post.ContentType.Should().Be("text/xml; charset=utf-8");
        post.Headers.Should().Contain(h => h.Key == "SOAPAction" && h.Value == "\"http://example.com/calc/Add\"");
        post.Body.Should().Contain("<soap:Body><Add").And.Contain("http://schemas.xmlsoap.org/soap/envelope/");
    }

    [Fact]
    public async Task Soap_fault_is_surfaced_as_an_error_and_unknown_operation_rejected()
    {
        var (service, _, transport) = Create();
        transport.Respond = r => r.Method == "GET"
            ? new TransportResponse(200, "text/xml", Wsdl, false, null)
            : new TransportResponse(500, "text/xml", "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><faultcode>s:Server</faultcode><faultstring>Boom</faultstring></s:Fault></s:Body></s:Envelope>", false, null);
        var saved = await service.SaveAsync(null, new IntegrationSaveRequest("Calc", IntegrationKind.Soap, true, "https://calc.example.com/svc?wsdl", IntegrationAuth.None, null, null, false, null, null, null));

        var (result, _) = await service.SoapInvokeAsync(saved.Integration!.Id, new SoapInvokeRequest("Add", "<Add/>"));
        result!.Error.Should().Be("SOAP fault: Boom");

        var (none, error) = await service.SoapInvokeAsync(saved.Integration.Id, new SoapInvokeRequest("Nope", "<Nope/>"));
        none.Should().BeNull();
        error.Should().Contain("Nope");
    }

    [Fact]
    public async Task A_wsdl_endpoint_pointing_at_an_internal_address_is_refused()
    {
        var (service, _, transport) = Create();
        var internalWsdl = Wsdl.Replace("https://calc.example.com/svc", "http://169.254.169.254/latest");
        transport.Respond = _ => new TransportResponse(200, "text/xml", internalWsdl, false, null);
        var saved = await service.SaveAsync(null, new IntegrationSaveRequest("Calc", IntegrationKind.Soap, true, "https://calc.example.com/svc?wsdl", IntegrationAuth.None, null, null, false, null, null, null));

        var (result, error) = await service.SoapInvokeAsync(saved.Integration!.Id, new SoapInvokeRequest("Add", "<Add/>"));
        result.Should().BeNull();
        error.Should().Contain("isn't allowed");
        transport.Sent.Should().OnlyContain(r => r.Method == "GET");
    }
}
