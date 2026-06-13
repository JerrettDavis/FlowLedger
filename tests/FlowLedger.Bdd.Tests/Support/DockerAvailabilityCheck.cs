using System.Net.Sockets;

namespace FlowLedger.Bdd.Tests.Support;

/// <summary>
/// Guards integration scenarios that require a live Docker daemon.
/// When Docker is not available the scenarios skip cleanly rather than fail,
/// allowing <c>dotnet test</c> to remain green in environments without Docker.
/// </summary>
internal static class DockerAvailability
{
    private static readonly Lazy<bool> _isAvailable = new(CheckDocker);

    public static bool IsAvailable => _isAvailable.Value;

    private static bool CheckDocker()
    {
        if (Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 } dockerHost)
        {
            _ = dockerHost;
            return true;
        }

        try
        {
            using var tcp = new TcpClient();
            var connect = tcp.BeginConnect("127.0.0.1", 2375, null, null);
            var success = connect.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(300));
            if (success)
            {
                tcp.EndConnect(connect);
                return true;
            }
        }
        catch
        {
            // ignored
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var pipe = new System.IO.Pipes.NamedPipeClientStream(
                    ".", "docker_engine", System.IO.Pipes.PipeDirection.InOut);
                pipe.Connect(300);
                return true;
            }
            catch
            {
                // ignored
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                const string socketPath = "/var/run/docker.sock";
                if (File.Exists(socketPath))
                {
                    return true;
                }
            }
            catch
            {
                // ignored
            }
        }

        return false;
    }
}

/// <summary>
/// xUnit fact attribute that skips the scenario when Docker is not available.
/// </summary>
internal sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            Skip = "Docker is not available in this environment. Scenario skipped.";
        }
    }
}
