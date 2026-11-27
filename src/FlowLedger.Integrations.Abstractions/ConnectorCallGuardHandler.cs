using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Integrations.Abstractions;

/// <summary>The live call limits a <see cref="ConnectorCallGuardHandler"/> enforces. A budget of zero or less means unlimited.</summary>
/// <param name="CallsPaused">Kill switch: when true nothing is sent.</param>
/// <param name="DailyBudget">Hard cap on calls per UTC day, counted from the provider call ledger.</param>
/// <param name="MonthlyBudget">Hard cap on calls per UTC month.</param>
public sealed record ConnectorCallPolicy(bool CallsPaused, int DailyBudget, int MonthlyBudget);

/// <summary>
/// Sits innermost in a connector HttpClient pipeline (inside the retry policy, so every real attempt is counted).
/// Before each call: honours the kill switch and the daily/monthly budgets. After each call: appends a row to the provider call
/// ledger. A ledger WRITE failure never fails a call we already paid for; a failed budget READ does (fail closed). One
/// implementation for every connector (ADR 0018, #100); a connector supplies only its id, pause setting name and live limits.
/// </summary>
public sealed partial class ConnectorCallGuardHandler : DelegatingHandler
{
    private readonly IProviderCallLedger _ledger;
    private readonly string _provider;
    private readonly string _pauseSetting;
    private readonly Func<ConnectorCallPolicy> _policy;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    /// <param name="ledger">Where calls are counted and recorded.</param>
    /// <param name="provider">The ledger key, equal to the connector id (see <see cref="ConnectorIds"/>).</param>
    /// <param name="pauseSetting">The configuration key of the kill switch, named in the refusal message.</param>
    /// <param name="policy">Read on every call so a configuration reload takes effect immediately.</param>
    /// <param name="logger">Receives ledger write failures.</param>
    /// <param name="time">Clock for the day and month windows; defaults to the system clock.</param>
    public ConnectorCallGuardHandler(
        IProviderCallLedger ledger,
        string provider,
        string pauseSetting,
        Func<ConnectorCallPolicy> policy,
        ILogger logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(logger);
        _ledger = ledger;
        _provider = provider;
        _pauseSetting = pauseSetting;
        _policy = policy;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var policy = _policy();
        if (policy.CallsPaused)
        {
            throw new ProviderCallBudgetExceededException($"{_provider} calls are paused ({_pauseSetting}=true). Nothing was sent.");
        }

        var now = _time.GetUtcNow();
        if (policy.DailyBudget > 0)
        {
            var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            var used = await _ledger.CountSinceAsync(_provider, today, cancellationToken).ConfigureAwait(false);
            if (used >= policy.DailyBudget)
            {
                throw new ProviderCallBudgetExceededException($"Daily {_provider} call budget reached ({used}/{policy.DailyBudget}). Nothing was sent.");
            }
        }

        if (policy.MonthlyBudget > 0)
        {
            var month = new DateTimeOffset(new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc));
            var used = await _ledger.CountSinceAsync(_provider, month, cancellationToken).ConfigureAwait(false);
            if (used >= policy.MonthlyBudget)
            {
                throw new ProviderCallBudgetExceededException($"Monthly {_provider} call budget reached ({used}/{policy.MonthlyBudget}). Nothing was sent.");
            }
        }

        var sw = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        string? error = null;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.GetType().Name;
            throw;
        }
        finally
        {
            try
            {
                await _ledger.RecordAsync(new ProviderCallRecord(
                    _provider,
                    request.Method.Method,
                    request.RequestUri?.AbsolutePath ?? "",
                    response is null ? null : (int)response.StatusCode,
                    sw.ElapsedMilliseconds,
                    response?.Content.Headers.ContentLength,
                    now,
                    error), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogLedgerRecordFailed(ex, _provider, request.Method.Method, request.RequestUri?.AbsolutePath);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to record {Provider} call in the ledger ({Method} {Path})")]
    private partial void LogLedgerRecordFailed(Exception exception, string provider, string method, string? path);
}
