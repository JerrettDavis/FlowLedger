using FlowLedger.Web.Components.Dialogs;
using MudBlazor;

namespace FlowLedger.Web.Shared;

/// <summary>One way to ask "are you sure?" before a destructive action, so no page hand-rolls dialog parameters (or skips the prompt).</summary>
public static class ConfirmationExtensions
{
    /// <summary>Shows a confirmation dialog; true only when the user confirms (Cancel, Escape and the close button all return false).</summary>
    public static async Task<bool> ConfirmAsync(
        this IDialogService dialogs, string title, string body, string confirmText = "Remove")
    {
        var parameters = new DialogParameters<ConfirmDialog>
        {
            { x => x.ContentText, body },
            { x => x.ConfirmText, confirmText },
        };

        var dialog = await dialogs.ShowAsync<ConfirmDialog>(title, parameters, new DialogOptions { CloseButton = true });
        var result = await dialog.Result;
        return result is { Canceled: false };
    }
}
