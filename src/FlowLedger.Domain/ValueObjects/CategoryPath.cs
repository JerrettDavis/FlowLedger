namespace FlowLedger.Domain.ValueObjects;

/// <summary>
/// Hierarchical category path using '/' as separator (e.g. "Food/Groceries").
/// Represents a user-defined category taxonomy. Immutable and equatable.
/// </summary>
public sealed class CategoryPath : IEquatable<CategoryPath>
{
    private const char Separator = '/';

    public string Value { get; }
    public IReadOnlyList<string> Segments { get; }

    public CategoryPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new Exceptions.EmptyStringException(nameof(value));

        Value = value.Trim();
        Segments = Value.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (Segments.Count == 0)
            throw new Exceptions.EmptyStringException(nameof(value));
    }

    public string TopLevel => Segments[0];
    public bool IsLeaf => Segments.Count == 1;

    public CategoryPath? Parent =>
        Segments.Count <= 1 ? null : new CategoryPath(string.Join(Separator, Segments.SkipLast(1)));

    // Equality based solely on the canonical path string.
    public bool Equals(CategoryPath? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is CategoryPath cp && Equals(cp);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public static bool operator ==(CategoryPath? left, CategoryPath? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(CategoryPath? left, CategoryPath? right) => !(left == right);

    public override string ToString() => Value;
}
