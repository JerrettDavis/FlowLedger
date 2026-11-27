using FlowLedger.Web.ApiClient;

namespace FlowLedger.Web.Shared;

/// <summary>
/// What a dialog shows after the API rejected its submit (#211): the per-field messages from a 400 problem document next to the
/// fields they belong to, and one summary line above the form. The dialog stays open so nothing the user typed is lost.
/// </summary>
public sealed class FormFeedback
{
    private Dictionary<string, string>? _fields;

    /// <summary>Summary shown above the form; null when there is nothing to report.</summary>
    public string? Summary { get; private set; }

    /// <summary>The API's message for <paramref name="field"/> (case-insensitive), or null.</summary>
    public string? For(string field) => _fields is not null && _fields.TryGetValue(field, out var message) ? message : null;

    /// <summary>True when the API reported an error for <paramref name="field"/>.</summary>
    public bool Has(string field) => For(field) is not null;

    /// <summary>Forgets the previous result; call when the user edits a field so a stale message does not linger.</summary>
    public void Clear()
    {
        _fields = null;
        Summary = null;
    }

    /// <summary>
    /// Records a failed submit. Returns true when it was an API or network failure the form can show (the dialog stays open);
    /// false for anything else, which the caller lets propagate.
    /// </summary>
    public bool TryCapture(Exception ex)
    {
        switch (ex)
        {
            case ApiClientException api:
                _fields = api.FieldErrors is { Count: > 0 }
                    ? new Dictionary<string, string>(api.FieldErrors, StringComparer.OrdinalIgnoreCase)
                    : null;
                Summary = _fields is null ? SnackbarExtensions.ProblemText(api) : "Fix the highlighted fields and try again.";
                return true;
            case HttpRequestException or InvalidOperationException:
                _fields = null;
                Summary = SnackbarExtensions.GenericFailure;
                return true;
            default:
                return false;
        }
    }
}
