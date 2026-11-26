using System.Globalization;
using System.Runtime.CompilerServices;

namespace FlowLedger.Web.Tests;

internal static class TestCulture
{
    /// <summary>Format helpers follow the thread culture; pin the default so tests do not depend on the host.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        var us = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = us;
        CultureInfo.DefaultThreadCurrentUICulture = us;
        CultureInfo.CurrentCulture = us;
    }
}
