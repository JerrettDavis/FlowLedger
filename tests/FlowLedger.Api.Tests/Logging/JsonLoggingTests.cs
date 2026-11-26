using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Api.Tests.Logging;

/// <summary><c>Logging:Format=json</c> makes the console emit one JSON object per line (#193).</summary>
[Collection("ConsoleRedirect")]
public sealed class JsonLoggingTests
{
    private static string Run(string? format)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:Format"] = format });
        builder.ConfigureStructuredLogging();

        var original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            using (var host = builder.Build())
            {
                var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("test");
                using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = "abc123" }))
                {
                    logger.LogInformation("hello {Who}", "world");
                }
            }
        }
        finally
        {
            Console.SetOut(original);
        }

        return captured.ToString();
    }

    [Fact]
    public void Json_format_writes_parseable_lines_with_scopes()
    {
        var line = Run("json").Split('\n', StringSplitOptions.RemoveEmptyEntries).First(l => l.Contains("hello"));
        using var doc = JsonDocument.Parse(line);
        doc.RootElement.GetProperty("Message").GetString().Should().Be("hello world");
        doc.RootElement.GetProperty("Scopes").GetRawText().Should().Contain("abc123");
    }

    [Fact]
    public void Default_format_is_not_json()
    {
        var output = Run(null);
        output.Should().NotStartWith("{");
    }
}

[CollectionDefinition("ConsoleRedirect", DisableParallelization = true)]
public sealed class ConsoleRedirectCollection;
