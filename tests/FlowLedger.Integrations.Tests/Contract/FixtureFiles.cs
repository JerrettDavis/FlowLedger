namespace FlowLedger.Integrations.Tests.Contract;

/// <summary>Reads the sanitised provider response fixtures copied next to the test binaries (see Fixtures/README.md).</summary>
internal static class FixtureFiles
{
    public static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath));

    /// <summary>The repository copy of a fixture, used only when regenerating snapshots.</summary>
    public static string SourcePath(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FlowLedger.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "tests", "FlowLedger.Integrations.Tests", "Fixtures", relativePath);
    }
}

/// <summary>An <see cref="HttpMessageHandler"/> that answers from a delegate, so provider tests never touch the network.</summary>
internal sealed class FixtureHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        return respond(request, body);
    }

    public static HttpResponseMessage Json(string json) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}
