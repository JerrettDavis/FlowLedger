using FlowLedger.Integrations.Quiltt;
using FlowLedger.Integrations.SimpleFin;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Tests;

public sealed class ProviderOptionsValidationTests
{
    private static ServiceProvider Build(Action<IServiceCollection, IConfiguration> register, params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();
        var services = new ServiceCollection().AddLogging();
        register(services, config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Defaults_are_valid()
    {
        using var quiltt = Build((s, c) => s.AddQuilttProvider(c));
        quiltt.GetRequiredService<IOptions<QuilttOptions>>().Value.Should().NotBeNull();
        using var simpleFin = Build((s, c) => s.AddSimpleFinProvider(c));
        simpleFin.GetRequiredService<IOptions<SimpleFinOptions>>().Value.Should().NotBeNull();
    }

    [Fact]
    public void Quiltt_rejects_unknown_environment()
    {
        using var sp = Build((s, c) => s.AddQuilttProvider(c), ("Quiltt:Environment", "prod"));
        var act = () => sp.GetRequiredService<IOptions<QuilttOptions>>().Value;
        act.Should().Throw<OptionsValidationException>().WithMessage("*sandbox*production*");
    }

    [Fact]
    public void Quiltt_rejects_non_positive_page_size()
    {
        using var sp = Build((s, c) => s.AddQuilttProvider(c), ("Quiltt:PageSize", "0"));
        var act = () => sp.GetRequiredService<IOptions<QuilttOptions>>().Value;
        act.Should().Throw<OptionsValidationException>().WithMessage("*PageSize*");
    }

    [Fact]
    public void SimpleFin_rejects_history_over_provider_cap_and_bad_url()
    {
        using var tooLong = Build((s, c) => s.AddSimpleFinProvider(c), ("SimpleFin:HistoryDays", "400"));
        var act = () => tooLong.GetRequiredService<IOptions<SimpleFinOptions>>().Value;
        act.Should().Throw<OptionsValidationException>().WithMessage("*HistoryDays*");

        using var badUrl = Build((s, c) => s.AddSimpleFinProvider(c), ("SimpleFin:AccessUrl", "not a url"));
        var act2 = () => badUrl.GetRequiredService<IOptions<SimpleFinOptions>>().Value;
        act2.Should().Throw<OptionsValidationException>().WithMessage("*AccessUrl*");
    }
}
