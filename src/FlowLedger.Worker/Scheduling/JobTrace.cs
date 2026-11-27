using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Quartz;

namespace FlowLedger.Worker.Scheduling;

/// <summary>
/// One trace per background job run (#193). Everything the run does (the per-tenant fan-out, EF/Npgsql commands, outbound HTTP) hangs off
/// the run's root <see cref="Activity"/>, and the run's correlation id (the trace id) is in the log scope, the span tag and the baggage,
/// so one id finds the logs and the spans of one job run. Started INSIDE the job (not from a Quartz listener, whose async-local changes do
/// not flow into the job body).
/// </summary>
internal static class JobTrace
{
    /// <summary>The <see cref="ActivitySource"/> name; subscribed to in the worker host's tracing setup.</summary>
    public const string SourceName = "FlowLedger.Worker";

    /// <summary>Tag / baggage key shared with the API's request correlation (<c>Microsoft.Extensions.Hosting.CorrelationId</c>).</summary>
    public const string CorrelationKey = "correlation.id";

    internal static readonly ActivitySource Source = new(SourceName);

    /// <summary>Runs <paramref name="body"/> inside a job-run span, with the run's correlation id in the log scope.</summary>
    public static ValueTask RunAsync(
        IJobExecutionContext context, ILogger logger, Func<CancellationToken, ValueTask> body, CancellationToken ct)
        => RunAsync(context.JobDetail.Key.Name, context.FireInstanceId, JobRetryScheduler.AttemptOf(context), logger, body, ct);

    public static async ValueTask RunAsync(
        string job, string fireInstanceId, int attempt, ILogger logger, Func<CancellationToken, ValueTask> body, CancellationToken ct)
    {
        // A job run is a root: it must not inherit whatever span happened to be ambient where the scheduler thread was started.
        using var activity = Source.StartActivity($"job {job}", ActivityKind.Internal, parentContext: default);
        var correlationId = activity?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        if (activity is not null)
        {
            activity.SetTag("job.name", job);
            activity.SetTag("job.fire_instance", fireInstanceId);
            activity.SetTag("job.attempt", attempt);
            activity.SetTag(CorrelationKey, correlationId);
            activity.AddBaggage(CorrelationKey, correlationId);
        }

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId, ["Job"] = job });
        try
        {
            await body(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            throw;
        }
    }

    /// <summary>A child span for one tenant's share of a run.</summary>
    public static Activity? StartTenant(Guid tenantId)
    {
        var span = Source.StartActivity("tenant work");
        span?.SetTag("tenant.id", tenantId.ToString());
        return span;
    }
}
