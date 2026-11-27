using FlowLedger.Api.Endpoints;

namespace FlowLedger.Api.Tests.Builders;

/// <summary>Builds <see cref="ImportRequest"/>: a header-row CSV with date, amount and description in columns 0 to 2.</summary>
public sealed class ImportRequestBuilder
{
    private Guid _accountId = Guid.NewGuid();
    private string _csv = string.Empty;
    private int _dateColumn;
    private int _amountColumn = 1;
    private int _descriptionColumn = 2;
    private int? _merchantColumn;
    private int? _categoryColumn;
    private string? _delimiter;
    private string[]? _dateFormats;
    private bool _hasHeaderRow = true;

    public static ImportRequestBuilder A() => new();

    public ImportRequestBuilder IntoAccount(Guid accountId) { _accountId = accountId; return this; }
    public ImportRequestBuilder OfCsv(string csv) { _csv = csv; return this; }
    public ImportRequestBuilder Columns(int date, int amount, int description) { _dateColumn = date; _amountColumn = amount; _descriptionColumn = description; return this; }
    public ImportRequestBuilder MerchantColumn(int? index) { _merchantColumn = index; return this; }
    public ImportRequestBuilder CategoryColumn(int? index) { _categoryColumn = index; return this; }
    public ImportRequestBuilder Delimited(string? delimiter) { _delimiter = delimiter; return this; }
    public ImportRequestBuilder DatedAs(params string[] formats) { _dateFormats = formats; return this; }
    public ImportRequestBuilder WithHeaderRow(bool hasHeaderRow) { _hasHeaderRow = hasHeaderRow; return this; }

    public ImportRequest Build() => new(
        _accountId, _csv, _dateColumn, _amountColumn, _descriptionColumn, _merchantColumn, _categoryColumn, _delimiter, _dateFormats, _hasHeaderRow);

    public static implicit operator ImportRequest(ImportRequestBuilder builder) => builder.Build();
}
