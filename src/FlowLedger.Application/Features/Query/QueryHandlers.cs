using FlowLedger.Application.Abstractions;
using FluentValidation;

namespace FlowLedger.Application.Features.Query;

internal sealed class SaveReportRequestValidator : AbstractValidator<SaveReportRequest>
{
    /// <summary>Cap on the serialized panel JSON of one saved item (#166), so one row cannot be made arbitrarily large.</summary>
    public const int MaxPayloadChars = 65_536;

    public SaveReportRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(100);
        RuleFor(r => r.Description).MaximumLength(500);
        RuleFor(r => r.Kind).Must(k => k is "View" or "Report").WithMessage("Kind must be View or Report.");
        RuleFor(r => r.Panels).NotNull().Must(p => p is { Count: >= 1 and <= 12 })
            .WithMessage("A saved item needs 1–12 panels.");
        RuleFor(r => r.Panels).Must(p => p is null || System.Text.Json.JsonSerializer.Serialize(p).Length <= MaxPayloadChars)
            .WithMessage($"A saved item may not exceed {MaxPayloadChars:N0} characters of panel definitions.");
        RuleFor(r => r).Must(r => r.Kind != "View" || r.Panels?.Count == 1)
            .WithMessage("A view holds exactly one panel; save several as a report.");
        RuleForEach(r => r.Panels).ChildRules(p =>
        {
            p.RuleFor(x => x.Title).NotEmpty().MaximumLength(100);
            p.RuleFor(x => x.Query).NotEmpty().MaximumLength(4000)
                .Must(IsParsable).WithMessage("The query does not parse.");
            p.RuleFor(x => x.Viz).NotNull();
            p.RuleFor(x => x.Viz.Kind).Must(k => VizSpec.Kinds.Contains(k)).When(x => x.Viz is not null)
                .WithMessage("Unknown visualization kind.");
        });
    }

    private static bool IsParsable(string query)
    {
        try { QueryParser.Parse(query); return true; }
        catch (QueryException) { return false; }
    }
}

/// <summary>Runs an ad-hoc query against the caller's own data.</summary>
public sealed class RunQueryHandler(QueryDatasetProvider datasets, TimeProvider clock, QueryBudget? budget = null)
{
    public async Task<QueryResult> HandleAsync(string query, CancellationToken ct = default)
        => QueryEngine.Execute(
            query, await datasets.BuildAsync(ct), DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), budget, ct);
}

public sealed class GetQuerySchemaHandler
{
    private static readonly string[] Operators =
        ["where", "project", "extend", "summarize … by", "sort by", "top N by", "take N", "distinct", "count"];

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "DI-resolved handler, invoked through the container like every other handler.")]
    public QuerySchemaDto Handle() => new(QueryCatalog.Datasets, QueryCatalog.Functions, QueryCatalog.Aggregates, Operators);
}

public sealed class ListReportsHandler(ISavedReportStore store)
{
    public async Task<IReadOnlyList<SavedReportDto>> HandleAsync(CancellationToken ct = default)
        => (await store.ListAsync(ct)).Concat(PresetReports.All).ToList();
}

public sealed class GetReportHandler(ISavedReportStore store)
{
    public async Task<SavedReportDto?> HandleAsync(string id, CancellationToken ct = default)
    {
        if (id.StartsWith(PresetReports.IdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return PresetReports.Find(id);
        }

        return Guid.TryParse(id, out var guid) ? await store.GetAsync(guid, ct) : null;
    }
}

public sealed class SaveReportHandler(ISavedReportStore store)
{
    public async Task<SavedReportDto> CreateAsync(SaveReportRequest request, CancellationToken ct = default)
    {
        TenantLimits.EnsureRoom((await store.ListAsync(ct)).Count, 1, TenantLimits.MaxSavedReports, "saved views and reports");
        return await store.CreateAsync(request, ct);
    }

    public Task<SavedReportDto?> UpdateAsync(Guid id, SaveReportRequest request, CancellationToken ct = default)
        => store.UpdateAsync(id, request, ct);

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => store.DeleteAsync(id, ct);
}

/// <summary>Runs every panel of a saved or preset report; a failing panel reports its error instead of failing the report.</summary>
public sealed class RunReportHandler(GetReportHandler reports, QueryDatasetProvider datasets, TimeProvider clock, QueryBudget? budget = null)
{
    /// <summary>
    /// Narrows a panel query to a date window by inserting a date filter right after the dataset name; only datasets that
    /// have a <c>date</c> column (transactions, balance_history) are narrowed, others run unchanged.
    /// </summary>
    internal static string ApplyRange(string query, DateOnly? from, DateOnly? to)
    {
        if (from is null && to is null)
        {
            return query;
        }

        var trimmed = query.TrimStart();
        var end = trimmed.IndexOf('|');
        var dataset = (end < 0 ? trimmed : trimmed[..end]).Trim();
        if (dataset is not ("transactions" or "balance_history"))
        {
            return query;
        }

        var clauses = new List<string>();
        if (from is { } f)
        {
            clauses.Add($"date >= \"{f:yyyy-MM-dd}\"");
        }

        if (to is { } t)
        {
            clauses.Add($"date <= \"{t:yyyy-MM-dd}\"");
        }

        var filter = $"| where {string.Join(" and ", clauses)} ";
        return end < 0 ? $"{dataset} {filter}" : $"{dataset} {filter}{trimmed[end..]}";
    }

    public async Task<RunReportResultDto?> HandleAsync(string id, CancellationToken ct = default, DateOnly? from = null, DateOnly? to = null)
    {
        var report = await reports.HandleAsync(id, ct);
        if (report is null)
        {
            return null;
        }

        var tables = await datasets.BuildAsync(ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var panels = report.Panels.Select(p =>
        {
            try
            {
                return new PanelResultDto(p.Title, p.Query, p.Viz, QueryEngine.Execute(ApplyRange(p.Query, from, to), tables, today, budget, ct), null);
            }
            catch (QueryException ex)
            {
                return new PanelResultDto(p.Title, p.Query, p.Viz, null, ex.Message);
            }
            catch (QueryBudgetExceededException ex)
            {
                return new PanelResultDto(p.Title, p.Query, p.Viz, null, ex.Message);
            }
        }).ToList();

        return new RunReportResultDto(report.Id, report.Name, report.Description, report.Kind, panels);
    }
}
