using System.Reflection;

namespace FlowLedger.Api;

/// <summary>
/// Version and commit this build was cut from. Release images set <c>FLOWLEDGER_VERSION</c> (the pushed tag) and
/// <c>FLOWLEDGER_COMMIT</c> (the tagged commit); other builds fall back to the assembly's informational version
/// (<c>1.2.3+abcdef</c>) and then to "unknown". No version number is defined in source.
/// </summary>
internal sealed record BuildInfo(string Version, string Commit)
{
    public static BuildInfo From(IConfiguration configuration)
        => Resolve(configuration["FLOWLEDGER_VERSION"], configuration["FLOWLEDGER_COMMIT"],
            typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    internal static BuildInfo Resolve(string? version, string? commit, string? informational)
    {
        var plus = informational?.IndexOf('+') ?? -1;
        var assemblyVersion = plus >= 0 ? informational![..plus] : informational;
        var assemblyCommit = plus >= 0 ? informational![(plus + 1)..] : null;

        return new BuildInfo(
            string.IsNullOrWhiteSpace(version) ? (string.IsNullOrWhiteSpace(assemblyVersion) ? "unknown" : assemblyVersion) : version.Trim(),
            string.IsNullOrWhiteSpace(commit) ? (string.IsNullOrWhiteSpace(assemblyCommit) ? "unknown" : assemblyCommit) : commit.Trim());
    }
}
