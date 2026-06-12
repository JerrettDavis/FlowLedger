using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Events;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Events;

/// <summary>
/// Structured-logging handler for <see cref="TransactionImported"/> events.
/// Provides an observable side effect that proves handler dispatch is wired end-to-end.
/// A real downstream handler (e.g. categorization trigger, budget alert) would be added
/// in later milestones; this handler is retained as a diagnostic baseline.
/// </summary>
internal sealed class TransactionImportedLoggingHandler : IDomainEventHandler<TransactionImported>
{
    private readonly ILogger<TransactionImportedLoggingHandler> _logger;

    public TransactionImportedLoggingHandler(ILogger<TransactionImportedLoggingHandler> logger)
        => _logger = logger;

    public Task HandleAsync(TransactionImported domainEvent, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Domain event: TransactionImported — Tenant={TenantId} Account={AccountId} " +
            "Transaction={TransactionId} Amount={Amount} Direction={Direction} Source={Source}",
            domainEvent.TenantId,
            domainEvent.AccountId,
            domainEvent.TransactionId,
            domainEvent.Amount,
            domainEvent.Direction,
            domainEvent.Source);

        return Task.CompletedTask;
    }
}
