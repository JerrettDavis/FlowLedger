using System.Diagnostics;
using FlowLedger.Integrations.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Mx;

/// <summary>
/// Sits innermost in the MX HttpClient pipeline (inside the retry policy, so every real attempt is counted).
/// Before each call: honours the kill switch (<c>Mx:Provider:CallsPaused</c>) and the daily/monthly budgets.
/// After each call: appends a row to the provider call ledger. A ledger WRITE failure never fails a call we
/// already paid for; a failed budget READ does (fail closed).
/// </summary>
public sealed partial class MxCallGuardHandler : DelegatingHandler
{
    public const string ProviderName = "MX";

    private readonly IProviderCallLedger _ledger;
    private readonly IOptionsMonitor<MxProviderOptions> _options;
    private readonly ILogger<MxCallGuardHandler> _logger;
    private readonly TimeProvider _time;

    public MxCallGuardHandler(
        IProviderCallLedger ledger,
        IOptionsMonitor<MxProviderOptions> options,
        ILogger<MxCallGuardHandler> logger,
        TimeProvider? time = null)
    {
        _ledger = ledger;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        if (opts.CallsPaused)
        {
            throw new ProviderCallBudgetExceededException("MX calls are paused (Mx:Provider:CallsPaused=true). Nothing was sent.");
        }

        var now = _time.GetUtcNow();
        if (opts.DailyCallBudget > 0)
        {
            var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            var used = await _ledger.CountSinceAsync(ProviderName, today, ct).ConfigureAwait(false);
            if (used >= opts.DailyCallBudget)
            {
                throw new ProviderCallBudgetExceededException($"Daily MX call budget reached ({used}/{opts.DailyCallBudget}). Nothing was sent.");
            }
        }

        if (opts.MonthlyCallBudget > 0)
        {
            var month = new DateTimeOffset(new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc));
            var used = await _ledger.CountSinceAsync(ProviderName, month, ct).ConfigureAwait(false);
            if (used >= opts.MonthlyCallBudget)
            {
                throw new ProviderCallBudgetExceededException($"Monthly MX call budget reached ({used}/{opts.MonthlyCallBudget}). Nothing was sent.");
            }
        }

        var sw = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        string? error = null;
        try
        {
            response = await base.SendAsync(request, ct).ConfigureAwait(false);
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
                    ProviderName,
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
                LogLedgerRecordFailed(ex, request.Method.Method, request.RequestUri?.AbsolutePath);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to record MX call in the ledger ({Method} {Path})")]
    private partial void LogLedgerRecordFailed(Exception exception, string method, string? path);
}
