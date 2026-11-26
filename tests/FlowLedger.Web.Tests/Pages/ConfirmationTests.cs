using System.Text.RegularExpressions;
using Bunit;
using FlowLedger.Web.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace FlowLedger.Web.Tests.Pages;

public sealed class ConfirmationTests : IAsyncLifetime
{
    // MudBlazor's services are async-disposable only, so the context must be disposed with DisposeAsync (xunit would call Dispose).
    private readonly BunitContext _ctx = new();

    public ConfirmationTests()
    {
        _ctx.Services.AddMudServices();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, Task<bool> Answer)> OpenAsync()
    {
        var provider = _ctx.Render<MudDialogProvider>();
        var dialogs = _ctx.Services.GetRequiredService<IDialogService>();
        var answer = dialogs.ConfirmAsync("Remove rule", "Remove this rule?", "Remove");
        await provider.WaitForAssertionAsync(() => provider.Markup.Should().Contain("Remove this rule?"));
        return (provider, answer);
    }

    [Fact]
    public async Task Cancel_returns_false()
    {
        var (provider, answer) = await OpenAsync();

        provider.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").Click();

        (await answer).Should().BeFalse();
    }

    [Fact]
    public async Task Confirm_returns_true()
    {
        var (provider, answer) = await OpenAsync();

        provider.FindAll("button").Single(b => b.TextContent.Trim() == "Remove").Click();

        (await answer).Should().BeTrue();
    }
}

/// <summary>Pages must not call destructive API methods without a confirmation (a mis-click on a trash icon must not discard configuration).</summary>
public sealed class DestructiveActionsAreConfirmedTests
{
    private static readonly Regex Destructive = new(
        @"ApiClient\.(Delete\w+Async|Remove\w+Async|ClearAccountConnectorAsync|MergeDuplicateAsync)\(", RegexOptions.Compiled);

    private static string ComponentsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "FlowLedger.Web", "Components")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull();
        return Path.Combine(dir!.FullName, "src", "FlowLedger.Web", "Components");
    }

    [Fact]
    public void Every_razor_file_that_calls_a_destructive_api_method_asks_first()
    {
        var offenders = Directory.EnumerateFiles(ComponentsDirectory(), "*.razor", SearchOption.AllDirectories)
            .Select(f => (File: f, Text: File.ReadAllText(f)))
            .Where(x => Destructive.IsMatch(x.Text))
            .Where(x => !x.Text.Contains("ConfirmAsync") && !x.Text.Contains("ConfirmDialog"))
            .Select(x => Path.GetFileName(x.File))
            .ToList();

        offenders.Should().BeEmpty("destructive actions must go through DialogService.ConfirmAsync");
    }
}
