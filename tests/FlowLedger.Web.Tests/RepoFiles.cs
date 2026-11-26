namespace FlowLedger.Web.Tests;

/// <summary>Reads source files from the repository, for lint-style tests that guard markup conventions.</summary>
internal static class RepoFiles
{
    public static string Root { get; } = FindRoot();

    public static string WebRoot => Path.Combine(Root, "src", "FlowLedger.Web");

    public static string Read(string relativeToWeb) =>
        File.ReadAllText(Path.Combine(WebRoot, relativeToWeb.Replace('/', Path.DirectorySeparatorChar)));

    public static IEnumerable<string> Razor() =>
        Directory.EnumerateFiles(Path.Combine(WebRoot, "Components"), "*.razor", SearchOption.AllDirectories);

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FlowLedger.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (FlowLedger.slnx) not found.");
    }
}
