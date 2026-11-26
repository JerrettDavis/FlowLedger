using FlowLedger.Web.ApiClient;
using MudBlazor;

namespace FlowLedger.Web.Shared;

public static class SnackbarExtensions
{
    public const string GenericFailure = "Something went wrong. Please try again.";

    /// <summary>
    /// The user-facing text for a failure: the API's problem-details message when the client produced one,
    /// otherwise a generic line, so raw framework or transport exception text never reaches the screen.
    /// </summary>
    public static string ProblemText(Exception ex, string? fallback = null) =>
        ex is ApiClientException { Message.Length: > 0 } api ? api.Message : fallback ?? GenericFailure;

    /// <summary>
    /// Shows a failure as an error that stays until dismissed (errors must not vanish before they can be read).
    /// An optional <paramref name="prefix"/> names the action, e.g. "Sync failed".
    /// </summary>
    public static Snackbar? AddFailure(this ISnackbar snackbar, Exception ex, string? prefix = null, string? fallback = null)
    {
        var text = ProblemText(ex, fallback);
        return snackbar.Add(prefix is null ? text : $"{prefix}: {text}", Severity.Error, ConfigureProblem);
    }

    /// <summary>How long an Undo offer stays on screen (long enough to notice and reach it by keyboard).</summary>
    public const int UndoVisibleMilliseconds = 10_000;

    /// <summary>
    /// Confirms a reversible action and offers Undo on the same toast. A failed undo is reported as a persistent error,
    /// never swallowed.
    /// </summary>
    public static Snackbar? AddWithUndo(this ISnackbar snackbar, string message, Func<Task> undo, string actionText = "Undo")
        => snackbar.Add(message, Severity.Success, o => ConfigureUndo(o, snackbar, undo, actionText));

    /// <summary>Wires the Undo action button onto a toast's options.</summary>
    public static void ConfigureUndo(SnackbarOptions o, ISnackbar snackbar, Func<Task> undo, string actionText = "Undo")
    {
        o.Action = actionText;
        o.ActionVariant = Variant.Text;
        o.ShowCloseIcon = true;
        o.VisibleStateDuration = UndoVisibleMilliseconds;
        o.OnClick = async _ =>
        {
            try
            {
                await undo();
                snackbar.Add("Change undone", Severity.Info);
            }
            catch (Exception ex)
            {
                snackbar.AddFailure(ex, "Couldn't undo");
            }
        };
    }

    /// <summary>Errors stay on screen with a close button instead of auto-dismissing.</summary>
    public static void ConfigureProblem(SnackbarOptions o)
    {
        o.RequireInteraction = true;
        o.ShowCloseIcon = true;
    }
}
