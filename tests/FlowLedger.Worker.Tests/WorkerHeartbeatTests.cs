using FlowLedger.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;

namespace FlowLedger.Worker.Tests;

public sealed class WorkerHeartbeatTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"fl-hb-{Guid.NewGuid():N}");

    [Fact]
    public void Missing_file_is_unhealthy()
        => WorkerHeartbeat.IsHealthy(TempFile(), TimeSpan.FromSeconds(60), TestClock.Instant).Should().BeFalse();

    [Fact]
    public void Fresh_file_is_healthy_and_stale_file_is_not()
    {
        var path = TempFile();
        try
        {
            var written = TestClock.Instant;
            File.WriteAllText(path, written.ToString("O"));

            WorkerHeartbeat.IsHealthy(path, TimeSpan.FromSeconds(60), written.AddSeconds(30)).Should().BeTrue();
            WorkerHeartbeat.IsHealthy(path, TimeSpan.FromSeconds(60), written.AddSeconds(120)).Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Garbage_file_is_unhealthy()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "not a timestamp");
            WorkerHeartbeat.IsHealthy(path, TimeSpan.FromSeconds(60), TestClock.Instant).Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Running_scheduler_writes_the_heartbeat()
    {
        var path = TempFile();
        // Minimal host (Quartz + heartbeat only): the full worker host also starts DB-dependent services.
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Worker:HeartbeatFile"] = path });
        builder.Services.AddQuartz();
        builder.Services.AddQuartzHostedService();
        builder.Services.AddSingleton(FlowLedger.TestSupport.TestClock.Provider);
        builder.Services.AddHostedService<WorkerHeartbeat>();
        using var host = builder.Build();
        try
        {
            await host.StartAsync();
            await Eventually.WaitAsync(() => File.Exists(path), "the heartbeat file is written", TimeSpan.FromSeconds(15));

            WorkerHeartbeat.IsHealthy(path, WorkerHeartbeat.MaxAge, FlowLedger.TestSupport.TestClock.Instant).Should().BeTrue();
        }
        finally
        {
            await host.StopAsync();
            File.Delete(path);
        }
    }
}
