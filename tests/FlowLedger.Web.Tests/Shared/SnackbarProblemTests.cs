using System.Net;
using Bunit;
using FlowLedger.Web.ApiClient;
using FlowLedger.Web.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace FlowLedger.Web.Tests.Shared;

public sealed class SnackbarProblemTests : BunitContext, IAsyncLifetime
{
    [Fact]
    public void Api_problem_text_is_shown_and_the_error_persists_until_dismissed()
    {
        Services.AddMudServices();
        var snackbar = Services.GetRequiredService<ISnackbar>();

        snackbar.AddFailure(new ApiClientException("The linked account was not found.", HttpStatusCode.BadRequest));

        var shown = snackbar.ShownSnackbars.Should().ContainSingle().Subject;
        shown.Message.Should().Be("The linked account was not found.");
        shown.Severity.Should().Be(Severity.Error);
    }

    [Fact]
    public void Problems_persist_until_dismissed()
    {
        var options = new SnackbarOptions(Severity.Error, new SnackbarConfiguration());

        SnackbarExtensions.ConfigureProblem(options);

        options.RequireInteraction.Should().BeTrue();
        options.ShowCloseIcon.Should().BeTrue();
    }

    [Fact]
    public void Non_api_exceptions_never_leak_their_raw_message()
    {
        var text = SnackbarExtensions.ProblemText(new InvalidOperationException("Sequence contains no elements at Foo.Bar()"));

        text.Should().Be(SnackbarExtensions.GenericFailure);
    }

    [Fact]
    public void Prefix_names_the_failed_action()
    {
        Services.AddMudServices();
        var snackbar = Services.GetRequiredService<ISnackbar>();

        snackbar.AddFailure(new HttpRequestException("boom"), "Sync failed");

        snackbar.ShownSnackbars.Single().Message.Should().Be($"Sync failed: {SnackbarExtensions.GenericFailure}");
    }

    [Fact]
    public async Task Undo_runs_the_reversal_and_confirms_it()
    {
        Services.AddMudServices();
        var snackbar = Services.GetRequiredService<ISnackbar>();
        var undone = false;
        var options = new SnackbarOptions(Severity.Success, new SnackbarConfiguration());

        SnackbarExtensions.ConfigureUndo(options, snackbar, () => { undone = true; return Task.CompletedTask; });

        options.Action.Should().Be("Undo");
        options.VisibleStateDuration.Should().Be(SnackbarExtensions.UndoVisibleMilliseconds);
        await options.OnClick!(null!);
        undone.Should().BeTrue();
        snackbar.ShownSnackbars.Should().Contain(s => s.Message == "Change undone");
    }

    [Fact]
    public async Task A_failing_undo_is_reported_as_a_persistent_error()
    {
        Services.AddMudServices();
        var snackbar = Services.GetRequiredService<ISnackbar>();
        var options = new SnackbarOptions(Severity.Success, new SnackbarConfiguration());

        SnackbarExtensions.ConfigureUndo(options, snackbar, () => throw new ApiClientException("Gone."));
        await options.OnClick!(null!);

        snackbar.ShownSnackbars.Should().ContainSingle(s => s.Severity == Severity.Error).Which.Message.Should().Be("Couldn't undo: Gone.");
    }

    [Fact]
    public void Add_with_undo_shows_the_message_as_a_success_toast()
    {
        Services.AddMudServices();
        var snackbar = Services.GetRequiredService<ISnackbar>();

        snackbar.AddWithUndo("Set to Food", () => Task.CompletedTask);

        snackbar.ShownSnackbars.Should().ContainSingle().Which.Message.Should().Be("Set to Food");
    }

    [Fact]
    public void No_page_shows_exception_messages_directly()
    {
        foreach (var file in RepoFiles.Razor())
        {
            var text = File.ReadAllText(file);
            text.Should().NotContain("Snackbar.Add(ex.Message", $"{Path.GetFileName(file)} must use Snackbar.AddProblem");
            text.Should().NotContain("{ex.Message}\", Severity.Error", $"{Path.GetFileName(file)} must use Snackbar.AddProblem");
            text.Should().NotContain("ex.Message", $"{Path.GetFileName(file)} must map exceptions via SnackbarExtensions.ProblemText");
        }
    }

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
}
