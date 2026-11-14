using FluentValidation;

namespace FlowLedger.Application.Features.Query;

/// <summary>Persistence for user-saved views and reports (tenant-scoped).</summary>
public interface ISavedReportStore
{
    Task<IReadOnlyList<SavedReportDto>> ListAsync(CancellationToken ct = default);
    Task<SavedReportDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task<SavedReportDto> CreateAsync(SaveReportRequest request, CancellationToken ct = default);
    Task<SavedReportDto?> UpdateAsync(Guid id, SaveReportRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}

public sealed class SaveReportRequestValidator : AbstractValidator<SaveReportRequest>
{
    public SaveReportRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(100);
        RuleFor(r => r.Description).MaximumLength(500);
        RuleFor(r => r.Kind).Must(k => k is "View" or "Report").WithMessage("Kind must be View or Report.");
        RuleFor(r => r.Panels).NotNull().Must(p => p is { Count: >= 1 and <= 12 })
            .WithMessage("A saved item needs 1–12 panels.");
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
public sealed class RunQueryHandler(QueryDatasetProvider datasets)
{
    public async Task<QueryResult> HandleAsync(string query, CancellationToken ct = default)
        => QueryEngine.Execute(query, await datasets.BuildAsync(ct), DateOnly.FromDateTime(DateTime.UtcNow));
}

public sealed class GetQuerySchemaHandler
{
    private static readonly string[] Operators =
        ["where", "project", "extend", "summarize … by", "sort by", "top N by", "take N", "distinct", "count"];

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
    public Task<SavedReportDto> CreateAsync(SaveReportRequest request, CancellationToken ct = default)
        => store.CreateAsync(request, ct);

    public Task<SavedReportDto?> UpdateAsync(Guid id, SaveReportRequest request, CancellationToken ct = default)
        => store.UpdateAsync(id, request, ct);

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => store.DeleteAsync(id, ct);
}

/// <summary>Runs every panel of a saved or preset report; a failing panel reports its error instead of failing the report.</summary>
public sealed class RunReportHandler(GetReportHandler reports, QueryDatasetProvider datasets)
{
    public async Task<RunReportResultDto?> HandleAsync(string id, CancellationToken ct = default)
    {
        var report = await reports.HandleAsync(id, ct);
        if (report is null)
        {
            return null;
        }

        var tables = await datasets.BuildAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var panels = report.Panels.Select(p =>
        {
            try
            {
                return new PanelResultDto(p.Title, p.Query, p.Viz, QueryEngine.Execute(p.Query, tables, today), null);
            }
            catch (QueryException ex)
            {
                return new PanelResultDto(p.Title, p.Query, p.Viz, null, ex.Message);
            }
        }).ToList();

        return new RunReportResultDto(report.Id, report.Name, report.Description, report.Kind, panels);
    }
}
