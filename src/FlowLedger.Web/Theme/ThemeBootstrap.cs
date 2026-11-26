namespace FlowLedger.Web.Theme;

/// <summary>
/// Inline head script that applies the stored (or system) colour scheme before first paint, so dark-mode
/// users do not see a white flash while the interactive circuit starts. It is rendered with the CSP nonce.
/// The matching CSS lives in app.css under <c>html[data-theme="dark"]</c>.
/// </summary>
public static class ThemeBootstrap
{
    public const string Script =
        "(function(){try{var t=localStorage.getItem('flowledger-theme');" +
        "var d=t?t==='dark':!!(window.matchMedia&&window.matchMedia('(prefers-color-scheme: dark)').matches);" +
        "var r=document.documentElement;r.setAttribute('data-theme',d?'dark':'light');" +
        "r.style.colorScheme=d?'dark':'light';}catch(e){}})();";
}
