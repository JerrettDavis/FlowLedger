using System.Globalization;
using FlowLedger.Web.Shared;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace FlowLedger.Web.Services;

/// <summary>Per-circuit holder of the display currency/locale; raises <see cref="Changed"/> on update.</summary>
public sealed class DisplaySettingsService
{
    public DisplaySettings Current { get; private set; } = DisplaySettings.Default;

    public event Action? Changed;

    public void Set(DisplaySettings settings)
    {
        if (settings == Current)
        {
            return;
        }

        Current = settings;
        Changed?.Invoke();
    }

    /// <summary>Makes the current thread format with the display culture.</summary>
    public void ApplyToCurrentThread() => CultureInfo.CurrentCulture = Current.ToCulture();
}

/// <summary>Applies the circuit's display culture around every inbound activity (renders, events, JS callbacks).</summary>
public sealed class DisplayCultureCircuitHandler(DisplaySettingsService settings) : CircuitHandler
{
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        context =>
        {
            settings.ApplyToCurrentThread();
            return next(context);
        };
}

/// <summary>Sets the request culture from the display-settings cookie (prerender and static pages).</summary>
public static class DisplayCultureMiddleware
{
    public static DisplaySettings ReadSettings(HttpContext http) =>
        DisplaySettings.Parse(http.Request.Cookies[DisplaySettings.CookieName]);

    public static IApplicationBuilder UseFlowLedgerDisplayCulture(this IApplicationBuilder app) =>
        app.Use((http, next) =>
        {
            CultureInfo.CurrentCulture = ReadSettings(http).ToCulture();
            return next();
        });
}
