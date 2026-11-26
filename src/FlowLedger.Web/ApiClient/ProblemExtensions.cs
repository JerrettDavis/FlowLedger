using System.Text.Json;

namespace FlowLedger.Web.ApiClient;

/// <summary>
/// Typed view of the connector-neutral ProblemDetails extension members (ADR 0018):
/// <c>issueKind</c>, <c>retryable</c>, <c>requiresUserAction</c>, <c>retryAfterSeconds</c>, <c>provider</c>.
/// </summary>
public sealed record ProblemExtensions(
    string? IssueKind, bool Retryable, bool RequiresUserAction, int? RetryAfterSeconds, string? Provider)
{
    public TimeSpan? RetryAfter => RetryAfterSeconds is > 0 ? TimeSpan.FromSeconds(RetryAfterSeconds.Value) : null;

    /// <summary>Reads the extensions from a problem document root; null when none are present.</summary>
    public static ProblemExtensions? From(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var kind = Str(root, "issueKind");
        var retryable = Bool(root, "retryable");
        var user = Bool(root, "requiresUserAction");
        var seconds = Int(root, "retryAfterSeconds");
        var provider = Str(root, "provider");
        return kind is null && !retryable && !user && seconds is null && provider is null
            ? null
            : new ProblemExtensions(kind, retryable, user, seconds, provider);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}

/// <summary>Parsed RFC 7807 problem response: safe text plus the typed issue extensions.</summary>
public sealed record ProblemDocument(string? Title, string? Detail, string? ValidationMessage, ProblemExtensions? Extensions)
{
    public static readonly ProblemDocument Empty = new(null, null, null, null);

    /// <summary>Parses a problem body; returns <see cref="Empty"/> for anything that is not a problem document.</summary>
    public static ProblemDocument Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Empty;
            }

            string? validation = null;
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var joined = string.Join(" ", errors.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.Array)
                    .SelectMany(p => p.Value.EnumerateArray())
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()));
                validation = joined.Length == 0 ? null : joined;
            }

            return new ProblemDocument(
                root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
                root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                validation,
                ProblemExtensions.From(root));
        }
        catch (JsonException)
        {
            return Empty;
        }
    }
}
