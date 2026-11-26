using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace FlowLedger.Worker.Tests;

public sealed class WorkerConfigValidatorTests
{
    private const string Conn = "Host=h;Database=d;Username=u;Password=p";

    private static IReadOnlyList<string> Validate(string env, params (string, string?)[] values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Item1, v => v.Item2)).Build();
        return WorkerConfigValidator.Validate(config, env);
    }

    [Fact]
    public void Valid_minimal_configuration_has_no_errors()
        => Validate(Environments.Production, ("ConnectionStrings:flowledger", Conn)).Should().BeEmpty();

    [Fact]
    public void Missing_connection_string_is_an_error()
        => Validate(Environments.Production).Should().ContainSingle().Which.Should().Contain("ConnectionStrings:flowledger");

    [Theory]
    [InlineData("ScheduledSync:CronExpression")]
    [InlineData("Backup:CronExpression")]
    [InlineData("AssetValuation:AutoRefresh:CronExpression")]
    [InlineData("HomeAssistant:CronExpression")]
    public void Invalid_cron_is_reported_with_the_offending_key(string key)
        => Validate(Environments.Production, ("ConnectionStrings:flowledger", Conn), (key, "every day at noon"))
            .Should().ContainSingle().Which.Should().Contain(key);

    [Fact]
    public void Valid_custom_cron_is_accepted()
        => Validate(Environments.Production, ("ConnectionStrings:flowledger", Conn), ("Backup:CronExpression", "0 30 2 * * ?")).Should().BeEmpty();

    [Fact]
    public void Non_boolean_enabled_flag_is_reported_instead_of_silently_using_the_default()
        => Validate(Environments.Production, ("ConnectionStrings:flowledger", Conn), ("Backup:Enabled", "flase"))
            .Should().ContainSingle().Which.Should().Contain("Backup:Enabled");

    [Fact]
    public void Bad_tenant_id_is_reported()
        => Validate(Environments.Production, ("ConnectionStrings:flowledger", Conn), ("Worker:TenantId", "not-a-guid"))
            .Should().ContainSingle().Which.Should().Contain("Worker:TenantId");

    [Fact]
    public void Require_offsite_without_a_repository_is_an_error_and_passes_with_one()
    {
        Validate(Environments.Production, ("ConnectionStrings:flowledger", Conn), ("Backup:RequireOffsite", "true"))
            .Should().ContainSingle().Which.Should().Contain("Backup:Restic:Repository");
        Validate(Environments.Production, ("ConnectionStrings:flowledger", Conn), ("Backup:RequireOffsite", "true"), ("Backup:Restic:Repository", "b2:bucket"))
            .Should().BeEmpty();
    }

    [Fact]
    public void Placeholder_secrets_are_rejected_outside_development_only()
    {
        var pairs = new (string, string?)[]
        {
            ("ConnectionStrings:flowledger", Conn),
            ("Mx:Enabled", "true"),
            ("Mx:ApiKey", "your-mx-api-key-here"),
            ("Mx:ClientId", "real-client"),
            ("Mx:WebhookSecret", "your-mx-webhook-secret-here"),
        };

        var prod = Validate(Environments.Production, pairs);
        prod.Should().HaveCount(2);
        prod.Should().Contain(e => e.Contains("Mx:ApiKey")).And.Contain(e => e.Contains("Mx:WebhookSecret"));

        Validate(Environments.Development, pairs).Should().BeEmpty();
    }

    [Fact]
    public void Mx_credentials_are_required_when_enabled_outside_development()
        => Validate(Environments.Production, ("ConnectionStrings:flowledger", Conn), ("Mx:Enabled", "true"))
            .Should().HaveCount(3);

    [Fact]
    public void Worker_host_refuses_to_build_with_an_invalid_cron_and_names_it()
    {
        var act = () => WorkerHostBuilderFactory.BuildTestHost(b =>
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:flowledger"] = Conn,
                ["Backup:CronExpression"] = "nonsense",
            }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Backup:CronExpression*");
    }
}
