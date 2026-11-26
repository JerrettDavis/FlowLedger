using FlowLedger.Web.ApiClient;
using MudBlazor;

namespace FlowLedger.Web.Connections;

/// <summary>A consistent, actionable message for a failed call.</summary>
public sealed record ToastModel(string Message, Severity Severity, bool OfferReconnect);

public static class ProblemToast
{
    /// <summary>Builds the toast for <paramref name="ex"/>; <paramref name="action"/> is a verb phrase such as "Sync".</summary>
    public static ToastModel From(Exception ex, string action, DateTimeOffset now)
    {
        var problem = (ex as ApiClientException)?.Problem;
        var view = ConnectionHealth.Describe(problem, now);
        switch (view.Category)
        {
            case ConnectionIssueCategory.UserAction:
                return new($"{view.Label}. {view.Detail}", Severity.Warning, true);

            case ConnectionIssueCategory.Transient:
                var rateLimited = string.Equals(problem?.IssueKind, "RateLimited", StringComparison.OrdinalIgnoreCase);
                var when = ConnectionHealth.RetryText(view.RetryAt, now);
                var message = rateLimited
                    ? $"Too many requests. {(when is null ? "Try again in a little while." : $"Try again {when}.")}"
                    : $"{ConnectionHealth.TransientLabel}.{(when is null ? string.Empty : $" Next attempt {when}.")}";
                return new(message, Severity.Info, false);

            default:
                return new($"{action} failed: {FlowLedger.Web.Shared.SnackbarExtensions.ProblemText(ex)}", Severity.Error, false);
        }
    }
}

public static class SnackbarProblemExtensions
{
    /// <summary>Shows <see cref="ProblemToast"/> for <paramref name="ex"/>; the Reconnect button invokes <paramref name="onReconnect"/>.</summary>
    public static void AddProblem(this ISnackbar snackbar, Exception ex, string action, Action? onReconnect = null)
    {
        var toast = ProblemToast.From(ex, action, DateTimeOffset.UtcNow);
        snackbar.Add(toast.Message, toast.Severity, config =>
        {
            if (toast.OfferReconnect && onReconnect is not null)
            {
                config.Action = "Reconnect";
                config.ActionVariant = Variant.Filled;
                config.OnClick = _ =>
                {
                    onReconnect();
                    return Task.CompletedTask;
                };
            }
        });
    }
}
