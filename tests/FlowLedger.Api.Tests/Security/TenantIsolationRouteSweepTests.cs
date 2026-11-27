using FluentAssertions;

namespace FlowLedger.Api.Tests.Security;

/// <summary>
/// INV-01 route sweep (#178), the symmetric half of <see cref="TenantIsolationMatrixTests"/>: both tenants hold data, and every
/// tenant-scoped GET route in <see cref="TenantIsolationManifest"/> must return only the caller's rows. Tenant A's responses may
/// contain none of B's sentinels or ids and B's none of A's, for each route individually, and on enough routes each caller must see
/// its own data so an empty body cannot pass for isolation. The route list comes from the manifest, so a new List route is swept
/// the moment it is added and a stale entry fails <see cref="TenantIsolationMatrixTests"/>.
/// </summary>
[Collection("ApiIntegration")]
[Trait("Invariant", "INV-01")]
public sealed class TenantIsolationRouteSweepTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private const string SecretB = "ZZ-B-SECRET";

    public Task InitializeAsync() => factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static IEnumerable<string> ListGetRoutes() =>
        TenantIsolationManifest.Routes
            .Where(kv => kv.Value.Kind == RouteKind.List && kv.Key.StartsWith("GET ", StringComparison.Ordinal))
            .Select(kv => kv.Key[4..]);

    [Fact]
    public async Task Every_tenant_scoped_GET_route_returns_only_the_callers_rows()
    {
        var a = factory.CreateAuthenticatedClient(TenantIsolationMatrixTests.TenantA);
        var b = factory.CreateAuthenticatedClient(TenantIsolationMatrixTests.TenantB);
        var seededA = await TenantIsolationMatrixTests.SeedTenantAAsync(a);
        var seededB = await TenantIsolationMatrixTests.SeedTenantAAsync(b, SecretB);

        var sentinelsA = new[] { TenantIsolationMatrixTests.Secret }.Concat(seededA.All.Select(g => g.ToString())).ToArray();
        var sentinelsB = new[] { SecretB }.Concat(seededB.All.Select(g => g.ToString())).ToArray();

        var routes = ListGetRoutes().ToList();
        routes.Should().NotBeEmpty("the manifest must declare tenant-scoped GET routes to sweep");

        var problems = new List<string>();
        var ownHitsA = 0;
        var ownHitsB = 0;
        foreach (var url in routes)
        {
            var asA = await a.GetAsync(url);
            var bodyA = await asA.Content.ReadAsStringAsync();
            var asB = await b.GetAsync(url);
            var bodyB = await asB.Content.ReadAsStringAsync();

            foreach (var (name, status, body) in new[] { ("A", (int)asA.StatusCode, bodyA), ("B", (int)asB.StatusCode, bodyB) })
            {
                if (status >= 500)
                {
                    problems.Add($"GET {url} as {name}: server error {status}");
                }
            }

            problems.AddRange(sentinelsB.Where(bodyA.Contains).Select(s => $"GET {url} as A leaked tenant B's '{s}'"));
            problems.AddRange(sentinelsA.Where(bodyB.Contains).Select(s => $"GET {url} as B leaked tenant A's '{s}'"));

            ownHitsA += sentinelsA.Any(bodyA.Contains) ? 1 : 0;
            ownHitsB += sentinelsB.Any(bodyB.Contains) ? 1 : 0;
        }

        problems.Should().BeEmpty(string.Join(Environment.NewLine, problems));
        ownHitsA.Should().BeGreaterThanOrEqualTo(8, "tenant A must see its own rows on most swept routes, otherwise the sweep proves nothing");
        ownHitsB.Should().BeGreaterThanOrEqualTo(8, "tenant B must see its own rows on most swept routes, otherwise the sweep proves nothing");
    }

    [Fact]
    public void Every_GET_route_is_classified_and_the_sweep_covers_the_tenant_scoped_ones()
    {
        var gets = TenantIsolationManifest.Routes.Where(kv => kv.Key.StartsWith("GET ", StringComparison.Ordinal)).ToList();

        gets.Where(kv => kv.Value.Kind == RouteKind.Gap).Select(kv => kv.Key).Should().BeEmpty("no GET route may be left unprobed");
        gets.Where(kv => kv.Value.Kind == RouteKind.Global).Should().OnlyContain(
            kv => !string.IsNullOrWhiteSpace(kv.Value.Note), "a Global GET route must say why it holds no tenant data");
        ListGetRoutes().Should().NotContain(u => u.Contains('{'), "List routes take no route parameters; ById routes are covered by the matrix test");
    }
}
