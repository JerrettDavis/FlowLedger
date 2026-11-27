using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Observability;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Events;

/// <summary>Tunables for <see cref="OutboxProcessor"/>.</summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Attempts before a message is dead-lettered.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Messages claimed per batch.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>How long a claim is held before another dispatcher may take the message (crash recovery).</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>First retry delay; doubles each attempt up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Worker poll interval in seconds.</summary>
    public int PollSeconds { get; set; } = 5;
}

/// <summary>
/// Delivers outbox messages (#92) for the current tenant to every <see cref="IDomainEventHandler{TEvent}"/>.
/// Delivery is at-least-once: messages are claimed with <c>FOR UPDATE SKIP LOCKED</c> plus a lease (so concurrent dispatchers never
/// take the same row and a crashed one is recovered when its lease expires). Handler success is recorded per handler, so a retry
/// re-runs only the handlers that failed. A failing handler is never swallowed: the message is rescheduled with exponential
/// backoff and dead-lettered after <see cref="OutboxOptions.MaxAttempts"/>. Handlers should still be idempotent, because a crash
/// between a handler finishing and its completion being recorded repeats that one handler.
/// </summary>
public sealed partial class OutboxProcessor
{
    private readonly FlowLedgerDbContext _db;
    private readonly IServiceProvider _services;
    private readonly ILogger<OutboxProcessor> _logger;
    private readonly OutboxOptions _options;
    private readonly TimeProvider _time;

    public OutboxProcessor(
        FlowLedgerDbContext db,
        IServiceProvider services,
        ILogger<OutboxProcessor> logger,
        OutboxOptions? options = null,
        TimeProvider? time = null)
    {
        _db = db;
        _services = services;
        _logger = logger;
        _options = options ?? new OutboxOptions();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Claims and delivers one batch for the current tenant. Returns the number of messages claimed.</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken ct = default)
    {
        var tenant = _db.CurrentTenantId
            ?? throw new InvalidOperationException("The outbox is dispatched per tenant; an unscoped context cannot claim messages.");
        var now = _time.GetUtcNow();
        var token = Guid.NewGuid();
        var leaseEnd = now + _options.Lease;
        var batch = _options.BatchSize;

        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE outbox_messages SET lock_token = {token}, locked_until_utc = {leaseEnd}
            WHERE id IN (
                SELECT id FROM outbox_messages
                WHERE tenant_id = {tenant} AND processed_utc IS NULL AND dead_lettered_utc IS NULL
                  AND next_attempt_utc <= {now} AND (locked_until_utc IS NULL OR locked_until_utc < {now})
                ORDER BY occurred_utc, id
                LIMIT {batch}
                FOR UPDATE SKIP LOCKED)
            """, ct);

        var claimed = await _db.OutboxMessages.AsNoTracking()
            .Where(m => m.LockToken == token)
            .OrderBy(m => m.OccurredUtc).ThenBy(m => m.Id)
            .ToListAsync(ct);

        foreach (var message in claimed)
        {
            await DeliverAsync(message, token, ct);
        }

        return claimed.Count;
    }

    private async Task DeliverAsync(OutboxMessageRecord message, Guid token, CancellationToken ct)
    {
        var completed = message.CompletedHandlers.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        string? error = null;

        try
        {
            var domainEvent = OutboxEventSerializer.Deserialize(message.EventType, message.Payload);
            var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(domainEvent.GetType());
            var handleMethod = handlerType.GetMethod(nameof(IDomainEventHandler<FlowLedger.SharedKernel.IDomainEvent>.HandleAsync))!;

            foreach (var handler in _services.GetServices(handlerType))
            {
                if (handler is null)
                {
                    continue;
                }

                var key = handler.GetType().FullName ?? handler.GetType().Name;
                if (completed.Contains(key))
                {
                    continue;
                }

                try
                {
                    // Handlers share this scope's context: a previous handler's failed (or half-staged) changes must never be committed by the next one's SaveChanges.
                    _db.ChangeTracker.Clear();
                    await (Task)handleMethod.Invoke(handler, [domainEvent, ct])!;
                    completed.Add(key);
                    var progress = string.Join(',', completed);
                    await _db.OutboxMessages.Where(m => m.Id == message.Id && m.LockToken == token)
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.CompletedHandlers, progress), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var inner = ex is System.Reflection.TargetInvocationException { InnerException: { } i } ? i : ex;
                    LogOutboxHandlerFailedForAttempt(_logger, inner, key, message.EventType, message.Id, message.Attempts + 1);
                    error = $"{key}: {inner.Message}";
                    _db.ChangeTracker.Clear();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogOutboxMessageCouldNotBe(_logger, ex, message.Id, message.EventType);
            error = ex.Message;
        }

        var now = _time.GetUtcNow();
        var scoped = _db.OutboxMessages.Where(m => m.Id == message.Id && m.LockToken == token);

        if (error is null)
        {
            await scoped.ExecuteUpdateAsync(s => s
                .SetProperty(m => m.ProcessedUtc, now)
                .SetProperty(m => m.LastError, (string?)null)
                .SetProperty(m => m.LockToken, (Guid?)null)
                .SetProperty(m => m.LockedUntilUtc, (DateTimeOffset?)null), ct);
            FlowLedgerTelemetry.RecordOutbox("delivered");
            return;
        }

        var attempts = message.Attempts + 1;
        if (attempts >= _options.MaxAttempts)
        {
            FlowLedgerTelemetry.RecordOutbox("dead_lettered");
            LogOutboxMessageDeadLetteredAfter(_logger, message.Id, message.EventType, attempts, error);
            await scoped.ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Attempts, attempts)
                .SetProperty(m => m.DeadLetteredUtc, now)
                .SetProperty(m => m.LastError, error)
                .SetProperty(m => m.LockToken, (Guid?)null)
                .SetProperty(m => m.LockedUntilUtc, (DateTimeOffset?)null), ct);
            return;
        }

        var delay = TimeSpan.FromTicks(Math.Min(
            _options.MaxBackoff.Ticks,
            (long)(_options.BaseBackoff.Ticks * Math.Pow(2, attempts - 1))));
        var next = now + delay;
        FlowLedgerTelemetry.RecordOutbox("retried");
        await scoped.ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Attempts, attempts)
            .SetProperty(m => m.NextAttemptUtc, next)
            .SetProperty(m => m.LastError, error)
            .SetProperty(m => m.LockToken, (Guid?)null)
            .SetProperty(m => m.LockedUntilUtc, (DateTimeOffset?)null), ct);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox handler {Handler} failed for {EventType} {MessageId} (attempt {Attempt}).")]
    private static partial void LogOutboxHandlerFailedForAttempt(ILogger logger, Exception exception, string? handler, string? eventType, Guid messageId, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} ({EventType}) could not be prepared for delivery.")]
    private static partial void LogOutboxMessageCouldNotBe(ILogger logger, Exception exception, Guid messageId, string? eventType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} ({EventType}) dead-lettered after {Attempts} attempts: {Error}")]
    private static partial void LogOutboxMessageDeadLetteredAfter(ILogger logger, Guid messageId, string? eventType, int attempts, string? error);
}
