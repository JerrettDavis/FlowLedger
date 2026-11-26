using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Api.Tests.Security;

/// <summary>
/// Route sweep (#158). Enumerates EVERY endpoint the Production host maps and asserts it is either protected (401 with no
/// credentials and with a wrong API key) or on the explicit, reviewed anonymous allow-list below. Adding an anonymous route, or
/// mapping one outside the protected pipeline, fails this test until the allow-list is changed on purpose.
/// </summary>
[Collection("ApiIntegration")]
public sealed class RouteAuthorizationSweepTests(FailClosedApiFactory factory)
{
    /// <summary>
    /// Reviewed anonymous endpoints ("METHOD pattern"). Probes are anonymous by design; webhooks authenticate by provider signature
    /// (HMAC) inside the handler. Everything else must require the API key.
    /// </summary>
    private static readonly HashSet<string> AnonymousAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET /health",
        "GET /alive",
        "GET /ready", // readiness probe (#184): deliberately anonymous, reveals only healthy/unhealthy
        "POST /api/integrations/mx/webhooks",
        "POST /api/integrations/quiltt/webhooks",
    };

    private sealed record Route(string Method, string Pattern, bool AllowsAnonymous)
    {
        public string Key => $"{Method} {Pattern}";
    }

    private List<Route> Routes() =>
        factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(e =>
            {
                var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
                var anonymous = e.Metadata.GetMetadata<IAllowAnonymous>() is not null;
                return methods.Select(m => new Route(m.ToUpperInvariant(), "/" + e.RoutePattern.RawText!.TrimStart('/'), anonymous));
            })
            .Where(r => r.Method != "OPTIONS" && r.Method != "HEAD")
            .DistinctBy(r => r.Key)
            .OrderBy(r => r.Key)
            .ToList();

    private static string Concrete(string pattern) =>
        Regex.Replace(pattern, @"\{\*?(?<name>[^}:?]+)(?::(?<constraint>[^}?]+))?\??\}", m =>
            m.Groups["constraint"].Value switch
            {
                "int" or "long" => "1",
                "bool" => "true",
                "datetime" => "2026-01-01",
                _ => "00000000-0000-0000-0000-000000000001",
            });

    [Fact]
    public void The_host_maps_a_meaningful_number_of_routes()
    {
        Routes().Count.Should().BeGreaterThan(50, "the sweep must actually see the API surface");
    }

    [Fact]
    public void Exactly_the_allow_listed_routes_are_marked_anonymous()
    {
        var anonymous = Routes().Where(r => r.AllowsAnonymous).Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        anonymous.Should().BeEquivalentTo(AnonymousAllowList,
            "adding (or removing) an anonymous route requires a deliberate change to the allow-list in this test");
    }

    [Fact]
    public async Task Every_route_outside_the_allow_list_rejects_missing_and_wrong_credentials()
    {
        var anonymousClient = factory.CreateClient();
        var wrongKeyClient = factory.CreateClient();
        wrongKeyClient.DefaultRequestHeaders.Add("Authorization", "Bearer definitely-not-the-key");
        wrongKeyClient.DefaultRequestHeaders.Add("X-Tenant-Id", FlowLedgerApiFactory.DemoTenantId.ToString());

        var failures = new List<string>();
        foreach (var route in Routes().Where(r => !AnonymousAllowList.Contains(r.Key)))
        {
            foreach (var (name, client) in new[] { ("no credentials", anonymousClient), ("wrong key", wrongKeyClient) })
            {
                using var request = new HttpRequestMessage(new HttpMethod(route.Method), Concrete(route.Pattern));
                if (route.Method is "POST" or "PUT" or "PATCH")
                {
                    request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                }

                var response = await client.SendAsync(request);
                if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                {
                    failures.Add($"{route.Key} ({name}) returned {(int)response.StatusCode}");
                }
            }
        }

        failures.Should().BeEmpty("every non-allow-listed route must require the API key");
    }

    [Fact]
    public async Task Allow_listed_probes_answer_without_credentials()
    {
        var client = factory.CreateClient();

        (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/alive")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
