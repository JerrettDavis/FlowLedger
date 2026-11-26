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
    public static Snackbar? AddProblem(this ISnackbar snackbar, Exception ex, string? prefix = null, string? fallback = null)
    {
        var text = ProblemText(ex, fallback);
        return snackbar.Add(prefix is null ? text : $"{prefix}: {text}", Severity.Error, ConfigureProblem);
    }

    /// <summary>Errors stay on screen with a close button instead of auto-dismissing.</summary>
    public static void ConfigureProblem(SnackbarOptions o)
    {
        o.RequireInteraction = true;
        o.ShowCloseIcon = true;
    }
}
