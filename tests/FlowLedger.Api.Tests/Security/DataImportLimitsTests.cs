using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Api.Tests.Security;

[Collection("ApiIntegration")]
public sealed class DataImportLimitsTests(FlowLedgerApiFactory factory)
{
    [Fact]
    public void Import_endpoint_declares_a_request_size_and_multipart_limit()
    {
        var endpoint = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/api/data/import");

        var size = endpoint.Metadata.GetMetadata<RequestSizeLimitAttribute>();
        var form = endpoint.Metadata.GetMetadata<RequestFormLimitsAttribute>();

        size.Should().NotBeNull();
        form.Should().NotBeNull();
        form!.MultipartBodyLengthLimit.Should().BeLessThanOrEqualTo(256L * 1024 * 1024);
    }
}
