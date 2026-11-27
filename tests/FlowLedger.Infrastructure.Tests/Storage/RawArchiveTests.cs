using System.Text;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Storage;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowLedger.Infrastructure.Tests.Storage;

/// <summary>#142: tenant-partitioned raw payload archive, opt-in sealing, per-tenant purge and opt-in retention.</summary>
public sealed class RawArchiveTests : IDisposable
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("""{"accounts":[{"guid":"ACT-1","name":"Secret Checking"}]}""");
    private static readonly byte[] KeyBytes = new byte[32].Select((_, i) => (byte)(i + 11)).ToArray();

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"FlowLedger-Raw-{Guid.NewGuid():N}");
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private LocalDiskObjectStorage Storage() =>
        new(Options.Create(new ObjectStorageOptions { RootPath = _root }), NullLogger<LocalDiskObjectStorage>.Instance);

    private static RawArchiveSettings Sealed(int retentionDays = 0, params byte[][] previous) =>
        new(new SecretKeyRing(KeyBytes, previous), retentionDays);

    private static RawPayloadContext Ctx(Guid? tenant) => new("GET", "/users/USR-1/accounts?page=1", 200, tenant);

    private static RawArchiveMaintenance Maintenance(LocalDiskObjectStorage storage, RawArchiveSettings? settings = null) =>
        new(storage, storage, settings ?? RawArchiveSettings.Default);

    [Fact]
    public async Task Keys_are_partitioned_by_tenant_with_disjoint_prefixes_and_an_unattributed_bucket()
    {
        var archive = new ObjectStorageRawPayloadArchive(Storage());

        var a = await archive.ArchiveAsync("MX", "GetAccounts", Payload, Ctx(_tenantA));
        var b = await archive.ArchiveAsync("MX", "GetAccounts", Payload, Ctx(_tenantB));
        var none = await archive.ArchiveAsync("MX", "Webhook", Payload, Ctx(null));

        a.Should().StartWith($"raw/MX/{_tenantA:N}/");
        b.Should().StartWith($"raw/MX/{_tenantB:N}/");
        none.Should().StartWith("raw/MX/_unattributed/");
        a.Should().NotStartWith($"raw/MX/{_tenantB:N}/");
        File.ReadAllBytes(Path.Combine(_root, a)).Should().Equal(Payload, "encryption is off by default");
    }

    [Fact]
    public async Task Sealed_objects_are_not_json_hide_the_content_and_open_transparently_while_plaintext_stays_readable()
    {
        var storage = Storage();
        var plain = await new ObjectStorageRawPayloadArchive(storage).ArchiveAsync("MX", "Old", Payload, Ctx(_tenantA));
        var sealedKey = await new ObjectStorageRawPayloadArchive(storage, Sealed()).ArchiveAsync("MX", "New", Payload, Ctx(_tenantA));

        var onDisk = await File.ReadAllBytesAsync(Path.Combine(_root, sealedKey));
        IRawPayloadDecryptor.IsEncrypted(onDisk).Should().BeTrue();
        var act = () => System.Text.Json.JsonDocument.Parse(onDisk);
        act.Should().Throw<System.Text.Json.JsonException>();
        Encoding.UTF8.GetString(onDisk).Should().NotContain("Secret Checking").And.NotContain("ACT-1");

        var maintenance = Maintenance(storage, Sealed());
        (await maintenance.ReadAsync(sealedKey)).Should().Equal(Payload);
        (await maintenance.ReadAsync(plain)).Should().Equal(Payload, "existing plaintext objects stay readable once encryption is on");
    }

    [Fact]
    public async Task A_sealed_object_does_not_open_for_another_tenant_or_without_its_key_and_a_previous_key_still_opens_it()
    {
        var storage = Storage();
        var key = await new ObjectStorageRawPayloadArchive(storage, Sealed()).ArchiveAsync("MX", "New", Payload, Ctx(_tenantA));
        var content = await File.ReadAllBytesAsync(Path.Combine(_root, key));

        Maintenance(storage, Sealed()).TryDecrypt("MX", _tenantB.ToString("N"), content).Should().BeNull("bound to tenant A");
        Maintenance(storage).TryDecrypt("MX", _tenantA.ToString("N"), content).Should().BeNull("no key configured");

        var rotated = new RawArchiveSettings(new SecretKeyRing(new byte[32].Select((_, i) => (byte)(i + 99)).ToArray(), [KeyBytes]), 0);
        Maintenance(storage, rotated).TryDecrypt("MX", _tenantA.ToString("N"), content).Should().Equal(Payload);
    }

    [Fact]
    public async Task Erasing_a_tenant_deletes_only_that_tenants_objects_and_sidecars()
    {
        var storage = Storage();
        var archive = new ObjectStorageRawPayloadArchive(storage);
        await archive.ArchiveAsync("MX", "One", Payload, Ctx(_tenantA));
        await archive.ArchiveAsync("Quiltt", "Two", Payload, Ctx(_tenantA));
        var keep = await archive.ArchiveAsync("MX", "Three", Payload, Ctx(_tenantB));
        var unattributed = await archive.ArchiveAsync("MX", "Hook", Payload, Ctx(null));

        var deleted = await Maintenance(storage).EraseTenantAsync(_tenantA);

        deleted.Should().Be(4, "two payloads and their two sidecars");
        File.Exists(Path.Combine(_root, keep)).Should().BeTrue();
        File.Exists(Path.Combine(_root, unattributed)).Should().BeTrue();
        Directory.Exists(Path.Combine(_root, "raw", "MX", _tenantA.ToString("N"))).Should().BeTrue("only files are removed");
        Directory.GetFiles(Path.Combine(_root, "raw"), "*", SearchOption.AllDirectories).Should().NotContain(f => f.Contains(_tenantA.ToString("N")) && File.Exists(f));
    }

    [Fact]
    public async Task Retention_is_off_by_default_and_deletes_only_objects_older_than_the_window_when_enabled()
    {
        var storage = Storage();
        var archive = new ObjectStorageRawPayloadArchive(storage);
        var old = await archive.ArchiveAsync("MX", "Old", Payload, Ctx(_tenantA));
        var fresh = await archive.ArchiveAsync("MX", "Fresh", Payload, Ctx(_tenantA));
        foreach (var f in Directory.GetFiles(Path.Combine(_root, Path.GetDirectoryName(old)!), "*Old*"))
        {
            File.SetLastWriteTimeUtc(f, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        (await Maintenance(storage).PruneConfiguredAsync(FlowLedger.TestSupport.TestClock.Instant)).Should().Be(0);
        File.Exists(Path.Combine(_root, old)).Should().BeTrue();

        var removed = await Maintenance(storage, Sealed(retentionDays: 30)).PruneConfiguredAsync(FlowLedger.TestSupport.TestClock.Instant);

        removed.Should().Be(2, "the old payload and its sidecar");
        File.Exists(Path.Combine(_root, old)).Should().BeFalse();
        File.Exists(Path.Combine(_root, fresh)).Should().BeTrue();
    }

    [Fact]
    public async Task Mx_replay_reads_tenant_partitioned_and_sealed_archives_and_legacy_layout()
    {
        var storage = Storage();
        var sealedSettings = Sealed();
        var request = new HttpRequestMessage(HttpMethod.Get, "http://never.invalid/users/USR-1/accounts?page=1");

        // Sealed, tenant-partitioned object.
        await new ObjectStorageRawPayloadArchive(storage, sealedSettings).ArchiveAsync("MX", "GetAccounts", Payload, Ctx(_tenantA));
        var withKey = new MxArchiveReplayHandler(Path.Combine(_root, "raw", "MX"), Maintenance(storage, sealedSettings));
        withKey.Count.Should().Be(1);
        using (var invoker = new HttpMessageInvoker(withKey))
        {
            var response = await invoker.SendAsync(request, CancellationToken.None);
            (await response.Content.ReadAsByteArrayAsync()).Should().Equal(Payload);
        }

        // Without the key the replay fails loudly instead of feeding ciphertext to the mapper.
        using var noKey = new HttpMessageInvoker(new MxArchiveReplayHandler(Path.Combine(_root, "raw", "MX")));
        var act = () => noKey.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://never.invalid/users/USR-1/accounts?page=1"), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Legacy layout (raw/MX/yyyy/MM/dd, no tenant folder) still replays.
        var legacy = Path.Combine(_root, "raw", "MX", "2026", "01", "02");
        Directory.CreateDirectory(legacy);
        await File.WriteAllBytesAsync(Path.Combine(legacy, "1-Old-x.json"), Payload);
        await File.WriteAllTextAsync(Path.Combine(legacy, "1-Old-x.meta.json"),
            """{"method":"GET","pathAndQuery":"/legacy","statusCode":200}""");
        using var legacyInvoker = new HttpMessageInvoker(new MxArchiveReplayHandler(Path.Combine(_root, "raw", "MX"), Maintenance(storage, sealedSettings)));
        var legacyResponse = await legacyInvoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://never.invalid/legacy"), CancellationToken.None);
        (await legacyResponse.Content.ReadAsByteArrayAsync()).Should().Equal(Payload);
    }

    [Fact]
    public async Task Listing_refuses_a_prefix_that_escapes_the_storage_root()
    {
        var storage = Storage();
        var act = async () =>
        {
            await foreach (var _ in storage.ListAsync("../outside"))
            {
            }
        };
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
