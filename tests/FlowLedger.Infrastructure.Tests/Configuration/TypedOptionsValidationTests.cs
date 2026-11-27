using FlowLedger.Infrastructure.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlowLedger.Infrastructure.Tests.Configuration;

public sealed class TypedOptionsValidationTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();
        return new ServiceCollection().AddFlowLedgerTypedOptions(config).BuildServiceProvider();
    }

    [Fact]
    public void Defaults_are_valid()
    {
        using var sp = Build();
        sp.GetRequiredService<IOptions<BackupOptions>>().Value.KeepLocalSnapshots.Should().Be(30);
        sp.GetRequiredService<IOptions<SyncOptions>>().Value.MinIntervalMinutes.Should().BeNull();
        sp.GetRequiredService<IOptions<HomeAssistantOptions>>().Value.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Valid_values_bind()
    {
        var key = Convert.ToBase64String(new byte[32]);
        using var sp = Build(("Backup:KeepLocalSnapshots", "7"), ("Backup:Restic:Repository", "b2:x:y"), ("Backup:RequireOffsite", "true"),
            ("Sync:MinIntervalMinutes", "0"), ("HomeAssistant:EncryptionKey", key));
        sp.GetRequiredService<IOptions<BackupOptions>>().Value.Restic.Repository.Should().Be("b2:x:y");
        sp.GetRequiredService<IOptions<HomeAssistantOptions>>().Value.EncryptionKey.Should().Be(key);
    }

    [Theory]
    [InlineData("Backup:KeepLocalSnapshots", "0", "KeepLocalSnapshots")]
    [InlineData("Backup:RequireOffsite", "true", "Restic:Repository")]
    [InlineData("Backup:Retention:KeepLocalSnapshots", "0", "Retention:KeepLocalSnapshots")]
    [InlineData("Backup:Retention:ResticKeepDaily", "-1", "ResticKeepDaily")]
    [InlineData("Backup:Retention:PremigrateMaxAgeDays", "-1", "PremigrateMaxAgeDays")]
    [InlineData("Sync:MinIntervalMinutes", "-5", "MinIntervalMinutes")]
    [InlineData("HomeAssistant:EncryptionKey", "not base64!!", "EncryptionKey")]
    [InlineData("HomeAssistant:EncryptionKey", "AAAA", "32 bytes")]
    public void Invalid_values_fail_with_a_message_naming_the_setting(string key, string value, string expected)
    {
        using var sp = Build((key, value));
        var section = key.Split(':')[0];
        Action read = section switch
        {
            "Backup" => () => _ = sp.GetRequiredService<IOptions<BackupOptions>>().Value,
            "Sync" => () => _ = sp.GetRequiredService<IOptions<SyncOptions>>().Value,
            _ => () => _ = sp.GetRequiredService<IOptions<HomeAssistantOptions>>().Value,
        };
        read.Should().Throw<OptionsValidationException>().WithMessage($"*{expected}*");
    }

    [Fact]
    public void Unparseable_values_fail_instead_of_silently_defaulting()
    {
        using var sp = Build(("Backup:Enabled", "maybe"));
        var read = () => sp.GetRequiredService<IOptions<BackupOptions>>().Value;
        read.Should().Throw<InvalidOperationException>();
    }
}
