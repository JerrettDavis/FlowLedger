namespace FlowLedger.Web.Services;

/// <summary>Source-generated startup log messages for top-level statements.</summary>
internal static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Auth:Oidc is not configured: running in single-household mode WITHOUT sign-in. Configure Auth:Oidc before exposing this app.")]
    internal static partial void OidcNotConfigured(ILogger logger);
}
