using FlowLedger.Application.Features.Goals;
using FlowLedger.Application.Features.Integrations;

namespace FlowLedger.TestSupport.Builders;

/// <summary>Builds <see cref="SaveGoalRequest"/>: a new goal "Goal" with a 1000.00 target and nothing saved.</summary>
public sealed class SaveGoalRequestBuilder
{
    private Guid? _id;
    private string _name = "Goal";
    private decimal _targetAmount = 1000m;
    private decimal _savedAmount;
    private DateOnly? _targetDate;
    private Guid? _linkedAccountId;

    public static SaveGoalRequestBuilder A() => new();

    public SaveGoalRequestBuilder WithId(Guid? id) { _id = id; return this; }
    public SaveGoalRequestBuilder Named(string name) { _name = name; return this; }
    public SaveGoalRequestBuilder Targeting(decimal targetAmount) { _targetAmount = targetAmount; return this; }
    public SaveGoalRequestBuilder Saved(decimal savedAmount) { _savedAmount = savedAmount; return this; }
    public SaveGoalRequestBuilder By(DateOnly? targetDate) { _targetDate = targetDate; return this; }

    public SaveGoalRequestBuilder LinkedTo(Guid? accountId) { _linkedAccountId = accountId; return this; }

    public SaveGoalRequest Build() => new(_id, _name, _targetAmount, _savedAmount, _targetDate, _linkedAccountId);

    public static implicit operator SaveGoalRequest(SaveGoalRequestBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="IntegrationSaveRequest"/>: an enabled SOAP integration "Calc" at a WSDL URL with no authentication.</summary>
public sealed class IntegrationSaveRequestBuilder
{
    private string _name = "Calc";
    private IntegrationKind _kind = IntegrationKind.Soap;
    private bool _enabled = true;
    private string _url = "https://calc.example.com/svc?wsdl";
    private IntegrationAuth _auth = IntegrationAuth.None;
    private string? _authUser;
    private string? _credential;
    private bool _clearCredential;
    private Dictionary<string, string>? _headers;
    private string[]? _events;
    private string? _soapEndpoint;

    public static IntegrationSaveRequestBuilder A() => new();

    public IntegrationSaveRequestBuilder Named(string name) { _name = name; return this; }
    public IntegrationSaveRequestBuilder OfKind(IntegrationKind kind) { _kind = kind; return this; }
    public IntegrationSaveRequestBuilder Enabled(bool enabled) { _enabled = enabled; return this; }
    public IntegrationSaveRequestBuilder At(string url) { _url = url; return this; }
    public IntegrationSaveRequestBuilder Authenticating(IntegrationAuth auth, string? user = null, string? credential = null) { _auth = auth; _authUser = user; _credential = credential; return this; }
    public IntegrationSaveRequestBuilder ClearingCredential(bool clear = true) { _clearCredential = clear; return this; }
    public IntegrationSaveRequestBuilder WithHeaders(Dictionary<string, string>? headers) { _headers = headers; return this; }
    public IntegrationSaveRequestBuilder ForEvents(params string[] events) { _events = events; return this; }
    public IntegrationSaveRequestBuilder SoapEndpoint(string? endpoint) { _soapEndpoint = endpoint; return this; }

    public IntegrationSaveRequest Build() => new(_name, _kind, _enabled, _url, _auth, _authUser, _credential, _clearCredential, _headers, _events, _soapEndpoint);

    public static implicit operator IntegrationSaveRequest(IntegrationSaveRequestBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="RestCallRequest"/>: <c>GET /x</c> with no query, headers, body or content type.</summary>
public sealed class RestCallRequestBuilder
{
    private string _method = "GET";
    private string? _path = "/x";
    private string? _query;
    private Dictionary<string, string>? _headers;
    private string? _body;
    private string? _contentType;

    public static RestCallRequestBuilder A() => new();

    public RestCallRequestBuilder Using(string method) { _method = method; return this; }
    public RestCallRequestBuilder To(string? path) { _path = path; return this; }
    public RestCallRequestBuilder WithQuery(string? query) { _query = query; return this; }
    public RestCallRequestBuilder WithHeaders(Dictionary<string, string>? headers) { _headers = headers; return this; }
    public RestCallRequestBuilder WithBody(string? body, string? contentType = null) { _body = body; _contentType = contentType; return this; }

    public RestCallRequest Build() => new(_method, _path, _query, _headers, _body, _contentType);

    public static implicit operator RestCallRequest(RestCallRequestBuilder builder) => builder.Build();
}

/// <summary>Builds <see cref="SoapInvokeRequest"/>: operation "Add" with an empty body element.</summary>
public sealed class SoapInvokeRequestBuilder
{
    private string _operation = "Add";
    private string _bodyXml = "<Add/>";
    private string? _version;

    public static SoapInvokeRequestBuilder A() => new();

    public SoapInvokeRequestBuilder Calling(string operation, string bodyXml) { _operation = operation; _bodyXml = bodyXml; return this; }
    public SoapInvokeRequestBuilder WithVersion(string? version) { _version = version; return this; }

    public SoapInvokeRequest Build() => new(_operation, _bodyXml, _version);

    public static implicit operator SoapInvokeRequest(SoapInvokeRequestBuilder builder) => builder.Build();
}
