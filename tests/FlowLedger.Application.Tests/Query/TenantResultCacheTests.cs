using FlowLedger.Application.Abstractions;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Query;

/// <summary>#160: per-tenant result cache is tenant-keyed, invalidated by the data version, TTL-limited and bounded.</summary>
public sealed class TenantResultCacheTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Second_read_is_served_from_cache_and_a_data_version_bump_forces_a_recompute()
    {
        var versions = new TenantDataVersion();
        var cache = new TenantResultCache(versions, new Clock(T0));
        var tenant = Guid.NewGuid();
        var computed = 0;
        Task<int> Compute() => Task.FromResult(++computed);

        var first = await cache.GetOrAddAsync(tenant, "k", Compute);
        var second = await cache.GetOrAddAsync(tenant, "k", Compute);
        versions.Bump(tenant);
        var third = await cache.GetOrAddAsync(tenant, "k", Compute);

        (first, second, third).Should().Be((1, 1, 2));
        computed.Should().Be(2, "the second read was a hit; only the write forced work");
    }

    [Fact]
    public async Task One_tenants_cached_payload_is_never_served_to_another_tenant()
    {
        var cache = new TenantResultCache(new TenantDataVersion(), new Clock(T0));
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var forA = await cache.GetOrAddAsync(a, "dashboard", () => Task.FromResult("tenant A data"));
        var forB = await cache.GetOrAddAsync(b, "dashboard", () => Task.FromResult("tenant B data"));
        var againA = await cache.GetOrAddAsync(a, "dashboard", () => Task.FromResult("recomputed"));

        forA.Should().Be("tenant A data");
        forB.Should().Be("tenant B data");
        againA.Should().Be("tenant A data");
    }

    [Fact]
    public async Task A_write_to_one_tenant_does_not_invalidate_another_tenants_entries()
    {
        var versions = new TenantDataVersion();
        var cache = new TenantResultCache(versions, new Clock(T0));
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await cache.GetOrAddAsync(b, "k", () => Task.FromResult(1));

        versions.Bump(a);

        (await cache.GetOrAddAsync(b, "k", () => Task.FromResult(99))).Should().Be(1);
    }

    [Fact]
    public async Task Entries_expire_after_the_ttl()
    {
        var clock = new Clock(T0);
        var cache = new TenantResultCache(new TenantDataVersion(), clock, ttl: TimeSpan.FromSeconds(30));
        var tenant = Guid.NewGuid();
        await cache.GetOrAddAsync(tenant, "k", () => Task.FromResult(1));

        clock.Now = T0.AddSeconds(31);

        (await cache.GetOrAddAsync(tenant, "k", () => Task.FromResult(2))).Should().Be(2);
    }

    [Fact]
    public async Task A_result_computed_while_a_write_landed_is_not_cached()
    {
        var versions = new TenantDataVersion();
        var cache = new TenantResultCache(versions, new Clock(T0));
        var tenant = Guid.NewGuid();

        await cache.GetOrAddAsync(tenant, "k", () =>
        {
            versions.Bump(tenant);   // a save lands while the (slow) query runs
            return Task.FromResult("stale");
        });

        (await cache.GetOrAddAsync(tenant, "k", () => Task.FromResult("fresh"))).Should().Be("fresh");
    }

    [Fact]
    public async Task Cache_and_version_map_stay_bounded_after_ten_times_the_limit_of_distinct_keys_and_tenants()
    {
        var versions = new TenantDataVersion(maxTenants: 50);
        var cache = new TenantResultCache(versions, new Clock(T0), maxEntries: 50);

        for (var i = 0; i < 500; i++)
        {
            await cache.GetOrAddAsync(Guid.NewGuid(), "k" + i, () => Task.FromResult(i));
        }

        cache.Count.Should().BeLessThanOrEqualTo(50);
        versions.TrackedTenants.Should().BeLessThanOrEqualTo(50);
    }

    [Fact]
    public void An_evicted_tenant_version_comes_back_as_a_fresh_stamp_so_old_entries_cannot_match()
    {
        var versions = new TenantDataVersion(maxTenants: 1);
        var a = Guid.NewGuid();
        var before = versions.Get(a);

        versions.Get(Guid.NewGuid());   // evicts a
        var after = versions.Get(a);

        after.Should().NotBe(before);
    }
}
