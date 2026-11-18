using System.Text;
using System.Text.RegularExpressions;

namespace FlowLedger.Application.Features.Query;

/// <summary>Turns a plain-English question into a pipeline query. Implemented against a LOCAL model; data never leaves the machine.</summary>
public interface IQueryTranslator
{
    /// <param name="system">Instructions + schema + examples.</param>
    /// <param name="conversation">Alternating user/assistant turns; the last is the user's latest message.</param>
    Task<string> CompleteAsync(string system, IReadOnlyList<(string Role, string Content)> conversation, CancellationToken ct = default);
}

public sealed record AskQueryRequest(string Question);

public sealed record AskQueryResult(string Question, string? Query, string? Error, int Attempts);

/// <summary>Builds the model prompt and cleans the model's reply. Pure, so it is unit-tested without a model.</summary>
public static class QueryPrompt
{
    public static string BuildSystem(
        IReadOnlyList<DatasetInfo> datasets, IReadOnlyList<string> functions, IReadOnlyList<string> aggregates,
        IReadOnlyList<string> categories, IReadOnlyList<string> accounts, DateOnly today)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You translate questions about a person's finances into ONE query in a Kusto-style pipeline language.");
        sb.AppendLine("Reply with ONLY the query. No explanation, no markdown fences. Read-only: you cannot change data.");
        sb.AppendLine($"Today is {today:yyyy-MM-dd}.");
        sb.AppendLine();
        sb.AppendLine("Language: `dataset | where cond | extend x = expr | summarize total = sum(amount), n = count() by key | sort by total desc | take 10`.");
        sb.AppendLine("Operators: where, project, extend, summarize ... by, sort by, top N by, take N, distinct, count.");
        sb.AppendLine("Conditions: and, or, not, == != < <= > >=, contains, startswith, endswith, in (...), between(a, b). Strings use double quotes.");
        sb.AppendLine("Durations: 30d, 2w, 6mo, 1y. Dates compare as \"2026-01-01\" strings. Use ago(30d), today(), month(date), startofmonth(date).");
        sb.AppendLine($"Aggregates: {string.Join(", ", aggregates)}");
        sb.AppendLine($"Functions: {string.Join(", ", functions)}");
        sb.AppendLine();
        foreach (var d in datasets)
        {
            sb.AppendLine($"Dataset `{d.Name}`: {d.Description}");
            foreach (var c in d.Columns)
            {
                sb.AppendLine($"  - {c.Name} ({c.Type}): {c.Description}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Spending means direction == \"Debit\"; income means direction == \"Credit\". Amounts are positive numbers.");
        sb.AppendLine("Exclude transfers between the person's own accounts from income/spending with is_transfer == false.");
        sb.AppendLine("After `summarize`, ONLY the group keys and the named aggregates exist as columns (date, amount, etc. are gone). Always name computed keys: `by month = month(date)`. Never `sort by` a function call; sort by a column name.");
        sb.AppendLine("As a pipeline operator, count takes NO parentheses: `| count`. `count()` is only an aggregate inside summarize. To count matching rows: `transactions | where ... | count`.");
        if (categories.Count > 0)
        {
            sb.AppendLine($"Category values seen in the data: {string.Join("; ", categories)}");
        }

        if (accounts.Count > 0)
        {
            sb.AppendLine($"Account names: {string.Join("; ", accounts)}");
        }

        sb.AppendLine();
        sb.AppendLine("Examples:");
        sb.AppendLine("Q: How much did I spend on dining out last month?");
        sb.AppendLine("transactions | where direction == \"Debit\" and category startswith \"Food/Dining\" and date >= startofmonth(ago(1mo)) and date < startofmonth(today()) | summarize total = sum(amount)");
        sb.AppendLine("Q: Top 10 merchants by spending in the last 90 days");
        sb.AppendLine("transactions | where direction == \"Debit\" and date >= ago(90d) | summarize total = sum(amount), n = count() by merchant | top 10 by total");
        sb.AppendLine("Q: Monthly spending trend this year");
        sb.AppendLine("transactions | where direction == \"Debit\" and is_transfer == false and date >= \"" + today.Year + "-01-01\" | summarize total = sum(amount) by month = month(date)");
        sb.AppendLine("Q: Show monthly income vs spending for the last 12 months");
        sb.AppendLine("transactions | where date >= ago(12mo) and is_transfer == false | summarize income = sumif(amount, direction == \"Credit\"), spending = sumif(amount, direction == \"Debit\") by month = month(date) | extend net = income - spending");
        return sb.ToString();
    }

    private static readonly Regex Think = new(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Fence = new(@"```(?:[a-zA-Z]*)\s*\n?(.*?)```", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Strips reasoning tags, code fences and a leading "Query:" label, leaving just the query text.</summary>
    public static string ExtractQuery(string reply)
    {
        var text = Think.Replace(reply ?? string.Empty, string.Empty).Trim();
        var fence = Fence.Match(text);
        if (fence.Success)
        {
            text = fence.Groups[1].Value.Trim();
        }

        text = Regex.Replace(text, @"^\s*(query|answer)\s*:\s*", string.Empty, RegexOptions.IgnoreCase);
        return text.Trim().TrimEnd(';').Trim();
    }
}

/// <summary>
/// Plain-English question to validated query. The model's output is only ever fed to the read-only in-memory query engine, and
/// it is parsed AND executed here; on a parse/run error the error is sent back to the model for up to <see cref="MaxAttempts"/> tries.
/// </summary>
public sealed class AskQueryHandler(IQueryTranslator translator, QueryDatasetProvider datasets)
{
    public const int MaxAttempts = 3;
    public const int MaxQuestionLength = 500;

    public async Task<AskQueryResult> HandleAsync(string question, CancellationToken ct = default)
    {
        question = (question ?? string.Empty).Trim();
        if (question.Length == 0)
        {
            return new AskQueryResult(question, null, "Ask a question first.", 0);
        }

        if (question.Length > MaxQuestionLength)
        {
            return new AskQueryResult(question, null, $"Keep the question under {MaxQuestionLength} characters.", 0);
        }

        var tables = await datasets.BuildAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var system = QueryPrompt.BuildSystem(
            QueryCatalog.Datasets, QueryCatalog.Functions, QueryCatalog.Aggregates,
            Distinct(tables, "transactions | where category != \"\" | distinct category | take 80", today),
            Distinct(tables, "accounts | distinct name | take 40", today), today);

        var turns = new List<(string Role, string Content)> { ("user", question) };
        string? lastError = null;
        string? lastQuery = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            string reply;
            try
            {
                reply = await translator.CompleteAsync(system, turns, ct);
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or InvalidOperationException) && !ct.IsCancellationRequested)
            {
                return new AskQueryResult(question, null,
                    "The local AI model isn't reachable. Make sure Ollama is running (ollama serve) and the configured model is installed.", attempt);
            }

            var query = QueryPrompt.ExtractQuery(reply);
            lastQuery = query;
            if (query.Length == 0 || query.Length > 4000)
            {
                lastError = "The model returned no usable query.";
            }
            else
            {
                try
                {
                    QueryEngine.Execute(query, tables, today);   // parse + semantic check against the real data
                    return new AskQueryResult(question, query, null, attempt);
                }
                catch (QueryException ex)
                {
                    lastError = ex.Message;
                }
            }

            turns.Add(("assistant", query));
            turns.Add(("user", $"That query failed: {lastError} Reply with a corrected query only."));
        }

        return new AskQueryResult(question, lastQuery, $"Couldn't turn that into a working query ({lastError}). Try rephrasing, or write it in the workbench.", MaxAttempts);
    }

    private static List<string> Distinct(IReadOnlyDictionary<string, QueryTable> tables, string query, DateOnly today)
    {
        try
        {
            return QueryEngine.Execute(query, tables, today).Rows
                .Select(r => r.FirstOrDefault()?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();
        }
        catch (QueryException)
        {
            return [];
        }
    }
}
