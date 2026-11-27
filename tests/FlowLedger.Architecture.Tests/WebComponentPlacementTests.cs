using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace FlowLedger.Architecture.Tests;

/// <summary>Keeps Components/Pages for routable pages only, so dialogs and building blocks stay easy to find and reuse.</summary>
public sealed class WebComponentPlacementTests
{
    private static readonly System.Reflection.Assembly Web = typeof(FlowLedger.Web.Components.Routes).Assembly;

    private static IEnumerable<Type> Components() =>
        Web.GetTypes().Where(t => typeof(IComponent).IsAssignableFrom(t) && !t.IsAbstract && t.Namespace is not null && !t.IsNested);

    private static bool IsRoutable(Type t) => t.GetCustomAttributes(typeof(RouteAttribute), inherit: false).Length > 0;

    [Fact]
    public void Components_under_Pages_are_all_routable()
    {
        var offenders = Components()
            .Where(t => t.Namespace == "FlowLedger.Web.Components.Pages")
            .Where(t => !IsRoutable(t))
            .Select(t => t.Name)
            .ToList();

        offenders.Should().BeEmpty("non-routable components belong in Components/Shared, Components/Dialogs or Components/Layout");
    }

    [Fact]
    public void Dialogs_live_in_the_Dialogs_namespace()
    {
        var offenders = Components()
            .Where(t => t.Name.EndsWith("Dialog", StringComparison.Ordinal))
            .Where(t => t.Namespace != "FlowLedger.Web.Components.Dialogs")
            .Select(t => t.FullName)
            .ToList();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Routable_components_are_only_under_Pages()
    {
        var offenders = Components()
            .Where(IsRoutable)
            .Where(t => t.Namespace != "FlowLedger.Web.Components.Pages")
            .Select(t => t.FullName)
            .ToList();

        offenders.Should().BeEmpty();
    }
}
