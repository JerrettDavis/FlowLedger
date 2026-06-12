using FlowLedger.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FlowLedger.Infrastructure.Persistence.Converters;

/// <summary>EF Core value converters for strongly-typed ID records (Guid wrappers).</summary>
internal static class StronglyTypedIdConverters
{
    public static readonly ValueConverter<TenantId, Guid> TenantIdConverter =
        new(id => id.Value, g => TenantId.From(g));

    public static readonly ValueConverter<AccountId, Guid> AccountIdConverter =
        new(id => id.Value, g => AccountId.From(g));

    public static readonly ValueConverter<TransactionId, Guid> TransactionIdConverter =
        new(id => id.Value, g => TransactionId.From(g));

    public static readonly ValueConverter<RecurringFlowId, Guid> RecurringFlowIdConverter =
        new(id => id.Value, g => RecurringFlowId.From(g));

    public static readonly ValueConverter<PlannedOccurrenceId, Guid> PlannedOccurrenceIdConverter =
        new(id => id.Value, g => PlannedOccurrenceId.From(g));

    public static readonly ValueConverter<CategoryId, Guid> CategoryIdConverter =
        new(id => id.Value, g => CategoryId.From(g));

    // Nullable variants used for optional FK columns.
    public static readonly ValueConverter<CategoryId?, Guid?> NullableCategoryIdConverter =
        new(
            id => id.HasValue ? id.Value.Value : (Guid?)null,
            g => g.HasValue ? CategoryId.From(g.Value) : (CategoryId?)null);

    public static readonly ValueConverter<PlannedOccurrenceId?, Guid?> NullablePlannedOccurrenceIdConverter =
        new(
            id => id.HasValue ? id.Value.Value : (Guid?)null,
            g => g.HasValue ? PlannedOccurrenceId.From(g.Value) : (PlannedOccurrenceId?)null);

    public static readonly ValueConverter<TransactionId?, Guid?> NullableTransactionIdConverter =
        new(
            id => id.HasValue ? id.Value.Value : (Guid?)null,
            g => g.HasValue ? TransactionId.From(g.Value) : (TransactionId?)null);
}
