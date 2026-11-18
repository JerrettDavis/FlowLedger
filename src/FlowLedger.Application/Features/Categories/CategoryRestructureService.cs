using FlowLedger.Application.Abstractions;

namespace FlowLedger.Application.Features.Categories;

/// <summary>A category (and its descendants) that keeps its identity but gets a new path.</summary>
public sealed record CategoryMove(Guid Id, string OldPath, string NewPath, string DisplayName, Guid? ParentId);

/// <summary>A category whose transactions/splits/flows/rules/budgets are folded into an already-existing category, then removed.</summary>
public sealed record CategoryMerge(Guid SourceId, string OldPath, Guid TargetId, string TargetPath);

public sealed record CategoryRestructurePlan(IReadOnlyList<CategoryMove> Moves, IReadOnlyList<CategoryMerge> Merges);

public sealed record CategoryRestructureResult(int Moved, int Merged, int TransactionsAffected, IReadOnlyList<CategoryMerge> Merges);

public interface ICategoryMaintenanceStore
{
    /// <summary>Applies the plan atomically (all or nothing). Returns the number of transactions that changed category.</summary>
    Task<int> ApplyAsync(CategoryRestructurePlan plan, CancellationToken ct = default);
}

/// <summary>
/// Wholesale category rename / move / merge as ONE operation: "make path X (and everything under it) live at path P".
/// If P is free the subtree is renamed/moved; wherever a path in the target already exists the source node is merged into it
/// (its transactions, splits, recurring flows, rules and budgets follow). System categories can't be moved or renamed away.
/// </summary>
public sealed class CategoryRestructureService(ICategoryRepository categories, ICategoryMaintenanceStore store)
{
    public const int MaxPathLength = 500;

    public static string NormalizePath(string? path)
    {
        var segments = (path ?? string.Empty).Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', segments);
    }

    /// <summary>
    /// Computes what moving <paramref name="oldPathInput"/> (a category and/or everything under that path prefix) to
    /// <paramref name="newPath"/> would do, without changing anything. A parent that only exists implicitly
    /// ("Food" when only "Food/Dining" has a row) can be moved too.
    /// </summary>
    public async Task<CategoryRestructurePlan> PlanAsync(string oldPathInput, string newPath, CancellationToken ct = default)
    {
        var all = await categories.ListAsync(ct);
        var oldPath = NormalizePath(oldPathInput);
        var target = NormalizePath(newPath);
        if (target.Length == 0)
        {
            throw new ArgumentException("A new name or location is required.");
        }

        if (target.Length > MaxPathLength)
        {
            throw new ArgumentException($"Category paths must be {MaxPathLength} characters or fewer.");
        }

        if (oldPath == target)
        {
            throw new ArgumentException("That is already this category's path.");
        }

        if (target.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A category can't be moved inside itself.");
        }

        bool InSubtree(string p) =>
            string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase) || p.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase);

        var subtree = all.Where(c => InSubtree(c.Path.Value)).OrderBy(c => c.Path.Value.Count(ch => ch == '/')).ToList();
        if (subtree.Count == 0)
        {
            throw new KeyNotFoundException("Category not found.");
        }

        if (subtree.Any(c => c.IsSystem))
        {
            throw new InvalidOperationException("System categories (or categories containing them) can't be moved or renamed.");
        }

        // path -> id for every category that is NOT being moved; grows as subtree nodes are placed
        var occupied = all.Where(c => !InSubtree(c.Path.Value))
            .GroupBy(c => c.Path.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (g.First().Id, g.First().Path.Value), StringComparer.OrdinalIgnoreCase);

        var moves = new List<CategoryMove>();
        var merges = new List<CategoryMerge>();
        foreach (var node in subtree)
        {
            var newNodePath = target + node.Path.Value[oldPath.Length..];
            if (occupied.TryGetValue(newNodePath, out var existing))
            {
                merges.Add(new CategoryMerge(node.Id, node.Path.Value, existing.Id, existing.Value));
                continue;
            }

            var oldLast = node.Path.Value.Split('/').Last();
            var newLast = newNodePath.Split('/').Last();
            var display = string.Equals(node.DisplayName, oldLast, StringComparison.OrdinalIgnoreCase) || string.Equals(node.Path.Value, oldPath, StringComparison.OrdinalIgnoreCase)
                ? newLast
                : node.DisplayName;
            var slash = newNodePath.LastIndexOf('/');
            Guid? parentId = slash > 0 && occupied.TryGetValue(newNodePath[..slash], out var parent) ? parent.Id : null;
            moves.Add(new CategoryMove(node.Id, node.Path.Value, newNodePath, display, parentId));
            occupied[newNodePath] = (node.Id, newNodePath);
        }

        return new CategoryRestructurePlan(moves, merges);
    }

    public async Task<CategoryRestructureResult> MoveAsync(string oldPath, string newPath, bool dryRun, CancellationToken ct = default)
    {
        var plan = await PlanAsync(oldPath, newPath, ct);
        var affected = dryRun ? 0 : await store.ApplyAsync(plan, ct);
        return new CategoryRestructureResult(plan.Moves.Count, plan.Merges.Count, affected, plan.Merges);
    }
}
