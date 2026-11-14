namespace FlowLedger.Application.Features.Query;

/// <summary>How a result set is drawn. <c>SeriesBy</c> pivots long-form rows into one series per distinct value.</summary>
public sealed record VizSpec(string Kind, string? X = null, List<string>? Y = null, string? SeriesBy = null, bool Stacked = false)
{
    public static readonly string[] Kinds = ["table", "column", "bar", "line", "area", "pie"];
}

/// <summary>One query plus how to draw it. A View is a single panel; a Report is several.</summary>
public sealed record ReportPanel(string Title, string Query, VizSpec Viz);

public sealed record SavedReportDto(
    string Id,
    string Name,
    string? Description,
    string Kind,
    bool IsPreset,
    List<ReportPanel> Panels,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record SaveReportRequest(string Name, string? Description, string Kind, List<ReportPanel> Panels);

public sealed record RunQueryRequest(string Query);

/// <summary>A panel's outcome: either a result or a user-readable error (one bad panel must not sink a report).</summary>
public sealed record PanelResultDto(string Title, string Query, VizSpec Viz, QueryResult? Result, string? Error);

public sealed record RunReportResultDto(string Id, string Name, string? Description, string Kind, List<PanelResultDto> Panels);

public sealed record QuerySchemaDto(
    IReadOnlyList<DatasetInfo> Datasets,
    IReadOnlyList<string> Functions,
    IReadOnlyList<string> Aggregates,
    IReadOnlyList<string> Operators);
