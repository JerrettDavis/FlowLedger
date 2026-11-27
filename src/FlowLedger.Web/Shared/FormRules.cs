using FlowLedger.Web.ApiClient;

namespace FlowLedger.Web.Shared;

/// <summary>
/// Why a form cannot be submitted yet, as text the page shows next to the (disabled) button. Null means it can be submitted.
/// One place for the wording so the buttons never go grey without saying why.
/// </summary>
public static class FormRules
{
    public static string? Goal(string? name, decimal target) =>
        string.IsNullOrWhiteSpace(name) ? "Enter a goal name to save."
        : target <= 0 ? "Enter a target amount above zero to save."
        : null;

    public static string? Budget(string? category) =>
        string.IsNullOrWhiteSpace(category) ? "Pick a category to save the budget." : null;

    public static string? Rule(string? pattern, string? category) =>
        string.IsNullOrWhiteSpace(pattern) ? "Enter the text a description must contain."
        : string.IsNullOrWhiteSpace(category) ? "Enter the category to assign."
        : null;

    public static string? Debt(int rowCount) =>
        rowCount == 0 ? "Add at least one debt to compare payoff strategies." : null;

    /// <summary>The API's message for <paramref name="field"/> when <paramref name="error"/> carries per-field validation errors.</summary>
    public static string? ServerError(ApiClientException? error, string field) => error?.FieldError(field);
}
