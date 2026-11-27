namespace FlowLedger.Api.Endpoints;

/// <summary>
/// The size and range limits the HTTP surface enforces, in one place (#121). Endpoint files do not carry numeric literals of 1,000 or
/// more (an architecture test scans for them): a limit worth enforcing is worth naming and finding here.
/// </summary>
internal static class ApiLimits
{
    private const int Kibibyte = 1024;
    private const int Mebibyte = 1024 * Kibibyte;

    /// <summary>Webhook payloads are small JSON events; anything larger is refused before signature work.</summary>
    public const int WebhookBodyBytes = Mebibyte;

    /// <summary>Read buffer for bounded body reads.</summary>
    public const int BodyReadBufferBytes = 16 * Kibibyte;

    /// <summary>Buffer size for streaming a generated file back to the client.</summary>
    public const int FileStreamBufferBytes = 4 * Kibibyte;

    /// <summary>Rows read per page when a transaction export streams.</summary>
    public const int ExportPageRows = 500;

    /// <summary>Entries the per-process webhook replay map holds before it evicts the oldest.</summary>
    public const int WebhookReplayEntries = 4 * Kibibyte;

    /// <summary>Largest request body a CSV transaction or bill-sheet import accepts.</summary>
    public const long CsvImportBytes = 5L * Mebibyte;

    /// <summary>Largest request body the auto-detecting transaction import accepts (a little more headroom than the plain CSV import).</summary>
    public const long ImportRequestBytes = 6L * Mebibyte;

    /// <summary>Largest export zip /api/data/import accepts (a household export is a few MB; this leaves ample headroom).</summary>
    public const long DataImportBytes = 256L * Mebibyte;

    /// <summary>Monthly provider-call budget assumed for Mx when none is configured.</summary>
    public const int DefaultMxMonthlyCallBudget = 5_000;

    /// <summary>Upper bound for a money amount a what-if simulation accepts.</summary>
    public const decimal SimulationAmountCeiling = 1_000_000_000m;

    /// <summary>Upper bound for a monthly money amount a what-if simulation accepts.</summary>
    public const decimal SimulationMonthlyCeiling = 10_000_000m;

    /// <summary>Upper bound for a monthly cash-flow figure (base or scenario) in a what-if simulation.</summary>
    public const decimal SimulationMonthlyFlowCeiling = 1_000_000m;

    /// <summary>Upper bound for a yearly spend or a minimum payment in a what-if simulation.</summary>
    public const decimal SimulationAnnualCeiling = 100_000_000m;

    /// <summary>Upper bound for an income-raise percentage in a what-if simulation.</summary>
    public const decimal SimulationIncomeRaisePercentCeiling = 1_000m;
}
