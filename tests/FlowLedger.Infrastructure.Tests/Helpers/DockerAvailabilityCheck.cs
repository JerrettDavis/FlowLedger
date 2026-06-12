using System.Net.Sockets;

namespace FlowLedger.Infrastructure.Tests.Helpers;

/// <summary>
/// Guards integration tests that require a live Docker daemon.
/// When Docker is not available the tests skip cleanly rather than fail,
/// allowing <c>dotnet test</c> to remain green in environments without Docker.
/// </summary>
internal static class DockerAvailability
{
    private static readonly Lazy<bool> _isAvailable = new(CheckDocker);

    public static bool IsAvailable => _isAvailable.Value;

    /// <summary>
    /// Attempts to connect to the Docker daemon socket or Windows pipe.
    /// Returns true only when Docker appears reachable.
    /// </summary>
    private static bool CheckDocker()
    {
        // Quick TCP probe of the default Docker socket port (2375 on Windows/Linux daemons).
        // For Unix socket environments, fallback to environment variable check.
        if (Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 } dockerHost)
        {
            // DOCKER_HOST is set; assume Docker is available.
            _ = dockerHost; // suppress unused warning
            return true;
        }

        // Probe localhost:2375 (Docker daemon TCP, enabled in Docker Desktop with "expose without TLS")
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

        // Probe default Docker Desktop named pipe (Windows)
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", "docker_engine", System.IO.Pipes.PipeDirection.InOut);
                pipe.Connect(300);
                return true;
            }
            catch
            {
                // ignored
            }
        }

        // Probe default Unix socket (Linux/macOS)
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var socketPath = "/var/run/docker.sock";
                if (System.IO.File.Exists(socketPath))
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
/// xUnit fact attribute that skips the test when Docker is not available.
/// </summary>
internal sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            Skip = "Docker is not available in this environment. Test skipped.";
        }
    }
}
