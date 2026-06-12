namespace FlowLedger.Application.Features.Forecasting;

/// <summary>
/// Deterministic forecasting engine interface (PLAN.md §11).
///
/// Implementations must be:
/// - Deterministic: same inputs always produce the same outputs.
/// - Pure: no I/O, no wall-clock reads, no randomness.
/// - Explainable: every projected balance change includes contributing items.
/// - Currency-safe: never silently mix currencies.
/// - Fast: a 3-year horizon over 10 accounts must complete in under 500 ms.
///
/// Determinism is guaranteed by the caller passing an explicit as-of date
/// via <see cref="ForecastRequest.AsOf"/>.
/// </summary>
public interface IForecastEngine
{
    /// <summary>
    /// Runs the deterministic forecast and returns a fully-explainable result.
    /// </summary>
    /// <param name="request">All inputs required to produce the forecast. No I/O is performed.</param>
    /// <returns>Immutable forecast result.</returns>
    /// <exception cref="ForecastInputException">
    /// Thrown when the request is malformed — e.g. missing starting balance for a requested account,
    /// currency mismatch across accounts in the same request, or empty horizon.
    /// </exception>
    ForecastResult Run(ForecastRequest request);
}
