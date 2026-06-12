using FlowLedger.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FlowLedger.Infrastructure.Persistence.Converters;

/// <summary>
/// Value converter for <see cref="CategoryPath"/>: stores the slash-separated path string directly.
/// </summary>
internal static class CategoryPathConverter
{
    public static readonly ValueConverter<CategoryPath, string> Instance =
        new(path => path.Value, value => new CategoryPath(value));
}
