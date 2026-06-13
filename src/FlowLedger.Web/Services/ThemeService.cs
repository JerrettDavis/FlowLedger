using Microsoft.Extensions.Logging;

namespace FlowLedger.Web.Services;

/// <summary>
/// Holds the application-wide dark-mode state and exposes a change event so
/// any component can react when the theme is toggled or loaded from storage.
/// </summary>
public sealed class ThemeService
{
    private readonly ILogger<ThemeService> _logger;

    /// <summary>
    /// Whether dark mode is currently active.
    /// </summary>
    public bool IsDarkMode { get; private set; }

    /// <summary>
    /// True while we are still waiting for the persisted preference to be read
    /// from localStorage (i.e. the first <c>OnAfterRenderAsync</c> has not yet
    /// completed).  Components can use this to avoid a flash-of-wrong-theme.
    /// </summary>
    public bool IsInitializing { get; private set; } = true;

    /// <summary>Raised whenever <see cref="IsDarkMode"/> changes.</summary>
    public event Action? ThemeChanged;

    public ThemeService(ILogger<ThemeService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Called once on first render with the value read from localStorage (or
    /// <c>null</c> if no explicit preference has been saved yet, in which case
    /// <paramref name="systemPreference"/> is used as the fallback).
    /// </summary>
    public void Initialize(bool? persistedPreference, bool systemPreference)
    {
        if (persistedPreference.HasValue)
        {
            _logger.LogDebug("Theme initialised from persisted preference: {Mode}",
                persistedPreference.Value ? "dark" : "light");
            IsDarkMode = persistedPreference.Value;
        }
        else
        {
            _logger.LogDebug("No persisted preference; using system preference: {Mode}",
                systemPreference ? "dark" : "light");
            IsDarkMode = systemPreference;
        }

        IsInitializing = false;
        ThemeChanged?.Invoke();
    }

    /// <summary>
    /// Applies an explicit user toggle.  Callers are responsible for persisting
    /// the new value to localStorage after calling this.
    /// </summary>
    public void SetDarkMode(bool isDark)
    {
        if (IsDarkMode == isDark)
        {
            return;
        }

        _logger.LogDebug("Theme toggled to {Mode}", isDark ? "dark" : "light");
        IsDarkMode = isDark;
        ThemeChanged?.Invoke();
    }

    /// <summary>
    /// Called by the system-preference callback on <c>MudThemeProvider</c> when
    /// the OS/browser preference changes at runtime — only honoured when no
    /// explicit user preference has been persisted.
    /// </summary>
    public void ApplySystemPreference(bool isDark, bool hasUserOverride)
    {
        if (hasUserOverride)
        {
            _logger.LogDebug(
                "System preference changed to {Mode} but user has an explicit override; ignoring",
                isDark ? "dark" : "light");
            return;
        }

        _logger.LogDebug("System preference changed to {Mode}; applying", isDark ? "dark" : "light");
        IsDarkMode = isDark;
        ThemeChanged?.Invoke();
    }
}
