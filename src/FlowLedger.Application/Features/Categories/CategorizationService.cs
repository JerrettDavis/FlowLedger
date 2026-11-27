using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.LocalAi;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.Categories;

/// <summary>Tracked-entity access for categorization (kept separate so the read-only repositories stay read-only).</summary>
public interface ICategorizationStore
{
    /// <summary>Newest-first tracked transactions (Planned/Skipped included; callers filter).</summary>
    Task<IReadOnlyList<Transaction>> LoadTrackedAsync(int max, CancellationToken ct = default);

    Task SaveAsync(CancellationToken ct = default);

    /// <summary>
    /// Newest-first transactions WITHOUT change tracking. Use for whole-history analysis; mutate the rows you change and pass
    /// them to <see cref="SaveChangedAsync"/>. Default falls back to the tracked load for simple stores.
    /// </summary>
    Task<IReadOnlyList<Transaction>> LoadAsync(int max, CancellationToken ct = default) => LoadTrackedAsync(max, ct);

    /// <summary>Persists only <paramref name="changed"/> rows (loaded via <see cref="LoadAsync"/>), in bounded batches. Default saves everything tracked.</summary>
    Task SaveChangedAsync(IReadOnlyCollection<Transaction> changed, CancellationToken ct = default) => SaveAsync(ct);

    /// <summary>One tracked transaction by id (null when absent). Default falls back to a full load for simple stores.</summary>
    async Task<Transaction?> FindTrackedAsync(Guid id, CancellationToken ct = default)
        => (await LoadTrackedAsync(TransactionQuota.ProcessingCap, ct)).FirstOrDefault(t => t.Id == id);

    /// <summary>Tracked real transactions that are uncategorized or auto-categorized (callers still filter precisely).</summary>
    async Task<IReadOnlyList<Transaction>> LoadTrackedUncategorizedOrAutoAsync(int max, CancellationToken ct = default)
        => await LoadTrackedAsync(max, ct);

    /// <summary>Tracked transactions whose category was set automatically.</summary>
    async Task<IReadOnlyList<Transaction>> LoadTrackedAutoCategorizedAsync(int max, CancellationToken ct = default)
        => await LoadTrackedAsync(max, ct);

    /// <summary>True when some real (not Planned/Skipped) transaction has no category.</summary>
    async Task<bool> AnyUncategorizedAsync(CancellationToken ct = default)
        => (await LoadTrackedAsync(TransactionQuota.ProcessingCap, ct)).Any(t => t.CategoryId is null && t.Status.IsReal());
}

public sealed record CategorizationResult(
    int Categorized, int ByLearnedMerchant, int ByKeyword, int Transfers, int StillUncategorized, int ByModel = 0, int Suggested = 0, int ByLlm = 0);

/// <summary>Tuning for the ML categorizer step (bound from the "Categorization" configuration section).</summary>
public sealed class CategorizationOptions
{
    /// <summary>
    /// Predictions at or above this probability are applied as Auto categories. Default 1.01 = auto-apply OFF (suggest only):
    /// held-out-merchant cross-validation on real data measured only ~60% precision at 0.70 and ~72% at 0.80.
    /// </summary>
    public double MlThreshold { get; set; } = 1.01;

    /// <summary>Predictions between this and <see cref="MlThreshold"/> are stored as one-click suggestions only.</summary>
    public double SuggestionFloor { get; set; } = 0.40;
}

/// <summary>
/// Fills in MISSING categories only: a transaction that already has a category (manual or earlier run) is never changed.
/// Order of precedence for an uncategorized transaction:
///  1. transfer between the user's own accounts  -> "Transfer"
///  2. learned merchant: the category the user (or an earlier run) already gave that merchant most often
///  3. built-in keyword rules (<see cref="AutoCategorizer"/>)
/// Safe to re-run any time; it is idempotent.
/// </summary>
public sealed class CategorizationService(
    ICategorizationStore store,
    ICategoryRepository categories,
    IAccountRepository accounts,
    ITenantContext tenant,
    CategorizationOptions? options = null,
    ITextEmbedder? embedder = null,
    LocalAiOptions? localAi = null,
    ILocalLlm? llm = null,
    IRowVersionGuard? guard = null)
{
    private readonly CategorizationOptions _options = options ?? new CategorizationOptions();
    private readonly LocalAiOptions _localAi = localAi ?? new LocalAiOptions();

    private const int Max = TransactionQuota.ProcessingCap;

    public Task<CategorizationResult> RunAsync(CancellationToken ct = default) => RunAsync(false, ct);

    /// <summary>
    /// <paramref name="allowLlm"/> lets the optional in-process language model adjudicate rows the embedder is unsure about
    /// (suggestions only, capped per run). The sync pipeline never passes true: CPU inference would stall every sync.
    /// </summary>
    public async Task<CategorizationResult> RunAsync(bool allowLlm, CancellationToken ct = default)
    {
        if (!await store.AnyUncategorizedAsync(ct))
        {
            return new CategorizationResult(0, 0, 0, 0, 0);
        }

        var txs = await store.LoadAsync(Max, ct);
        var real = txs.Where(t => TransactionStatusExtensions.IsReal(t.Status)).ToList();
        var uncategorized = real.Where(t => t.CategoryId is null).ToList();
        if (uncategorized.Count == 0)
        {
            return new CategorizationResult(0, 0, 0, 0, 0);
        }

        var accountTypes = (await accounts.ListAsync(ct)).ToDictionary(a => a.Id, a => a.AccountType.ToString());
        var transferIds = TransferDetector.Detect(real.Select(TransactionMapper.ToDto).ToList(), accountTypes);

        // merchant -> the category the USER chose most often (auto-filled rows are not evidence of intent)
        var learned = real.Where(t => t.CategoryId is not null && t.CategorySource == CategorySource.Manual)
            .GroupBy(t => AutoCategorizer.MerchantKey(t.MerchantName, t.Description))
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.GroupBy(t => t.CategoryId!.Value).OrderByDescending(x => x.Count()).First().Key);

        var existing = (await categories.ListAsync(ct)).ToDictionary(c => c.Path.Value, c => c, StringComparer.OrdinalIgnoreCase);
        var tenantId = TenantId.From(tenant.TenantId);
        async Task<CategoryId> EnsureAsync(string path)
        {
            if (existing.TryGetValue(path, out var c))
            {
                return c.CategoryId;
            }

            var created = Category.Create(tenantId, new CategoryPath(path), path.Split('/').Last());
            await categories.AddAsync(created, ct);
            existing[path] = created;
            return created.CategoryId;
        }

        // ML step: trained on the user's own manual categories (Transfer excluded). Null when there is too little data.
        var pathById = existing.Values.ToDictionary(c => c.CategoryId, c => c.Path.Value);
        var model = NaiveBayesCategoryPredictor.Train(real
            .Where(t => t.CategoryId is not null && t.CategorySource == CategorySource.Manual)
            .Select(t => (t, path: pathById.GetValueOrDefault(t.CategoryId!.Value)))
            .Where(x => x.path is not null && !string.Equals(x.path, AutoCategorizer.TransferCategory, StringComparison.OrdinalIgnoreCase))
            .Select(x => new TrainingExample(x.t.MerchantName, x.t.Description, x.t.Direction.ToString(), x.t.Amount.Amount, x.path!)));

        // Semantic step (in-process embeddings): ranks unseen merchants by similarity to ones the user already categorized.
        var ranked = await RankSemanticAsync(real, uncategorized, existing.Values, ct);
        var llmReady = allowLlm && llm is { State: { Enabled: true, ModelInstalled: true } };
        var llmCalls = 0;
        // Few-shot examples for the model: merchants the user labelled by hand, per category (a handful each).
        var shots = llmReady && _localAi.Llm.FewShot
            ? real.Where(t => t.CategorySource == CategorySource.Manual && t.CategoryId is not null)
                .Select(t => (Path: existing.Values.FirstOrDefault(c => c.Id == t.CategoryId!.Value.Value)?.Path.Value, Text: SemanticTraining.TextOf(t)))
                .Where(x => x.Path is not null && x.Text.Length > 0)
                .GroupBy(x => x.Path!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Text).Distinct().Take(2).ToList(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, List<string>>();
        // Fallback offering for the model when the embedder has nothing: the categories the user relies on most.
        var popular = real.Where(t => t.CategorySource == CategorySource.Manual && t.CategoryId is not null)
            .GroupBy(t => t.CategoryId!.Value.Value).OrderByDescending(g => g.Count()).Take(6)
            .Select(g => existing.Values.FirstOrDefault(c => c.Id == g.Key)?.Path.Value)
            .Where(x => x is not null && !string.Equals(x, AutoCategorizer.TransferCategory, StringComparison.OrdinalIgnoreCase))
            .Select(x => x!).ToList();

        int byLearned = 0, byKeyword = 0, byModel = 0, byLlm = 0, suggested = 0, transfers = 0, left = 0;
        var changedRows = new List<Transaction>();
        foreach (var t in uncategorized)
        {
            var before = (t.CategoryId, t.CategorySource, t.SuggestedCategoryId, t.SuggestedConfidence);
            var key = AutoCategorizer.MerchantKey(t.MerchantName, t.Description);
            if (transferIds.Contains(t.Id))
            {
                t.Categorize(await EnsureAsync(AutoCategorizer.TransferCategory), null, CategorySource.Auto);
                transfers++;
            }
            else if (learned.TryGetValue(key, out var learnedId))
            {
                t.Categorize(learnedId, null, CategorySource.Auto);
                byLearned++;
            }
            else if (AutoCategorizer.Categorize(t.MerchantName, t.Description, t.Direction.ToString()) is { } path)
            {
                t.Categorize(await EnsureAsync(path), null, CategorySource.Auto);
                byKeyword++;
            }
            else
            {
                var bayes = model?.Predict(t.MerchantName, t.Description, t.Direction.ToString(), t.Amount.Amount);
                var text = SemanticTraining.TextOf(t);
                var candidates = ranked.GetValueOrDefault(text);
                var top = candidates is { Count: > 0 } ? candidates[0] : null;
                var sem = top is not null && top.Probability >= _localAi.SemanticFloor ? top : null;
                var p = PredictionEnsemble.Combine(bayes, sem, embedder?.IsSemantic == true);
                if (p is not null && p.Probability >= _options.MlThreshold)
                {
                    t.Categorize(await EnsureAsync(p.Label), null, CategorySource.Auto, p.Probability);
                    byModel++;
                }
                else if (p is not null && p.Probability >= _options.SuggestionFloor)
                {
                    t.SetSuggestion((await EnsureAsync(p.Label)), p.Probability);
                    suggested++;
                    left++;
                }
                else if (llmReady && llmCalls < _localAi.Llm.MaxCallsPerRun && text.Length > 0
                    && await AskLlmAsync(t, candidates, popular, shots, p, ct) is { } llmPath)
                {
                    llmCalls++;
                    t.SetSuggestion(await EnsureAsync(llmPath), LlmConfidence);
                    byLlm++;
                    suggested++;
                    left++;
                }
                else
                {
                    if (llmReady && llmCalls < _localAi.Llm.MaxCallsPerRun && text.Length > 0)
                    {
                        llmCalls++;
                    }

                    t.SetSuggestion(null, null);
                    left++;
                }
            }

            if ((t.CategoryId, t.CategorySource, t.SuggestedCategoryId, t.SuggestedConfidence) != before)
            {
                changedRows.Add(t);
            }
        }

        await categories.SaveChangesAsync(ct);
        await store.SaveChangedAsync(changedRows, ct);
        return new CategorizationResult(byLearned + byKeyword + byModel + transfers, byLearned, byKeyword, transfers, left, byModel, suggested, byLlm);
    }

    /// <summary>Fixed confidence shown for a language-model pick (it has no calibrated probability).</summary>
    private const double LlmConfidence = 0.5;

    /// <summary>Embedding-kNN top candidates keyed by normalised text; empty when disabled, under-trained, or the embedder fails.</summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<Prediction>>> RankSemanticAsync(
        List<Transaction> real, List<Transaction> uncategorized, IEnumerable<Category> allCategories, CancellationToken ct)
    {
        var none = new Dictionary<string, IReadOnlyList<Prediction>>();
        if (embedder is null || !_localAi.Enabled)
        {
            return none;
        }

        try
        {
            var pathById = allCategories.ToDictionary(c => c.Id, c => c.Path.Value);
            var examples = SemanticTraining.Examples(
                real.Where(t => t.CategorySource == CategorySource.Manual), pathById, embedder.IsSemantic);
            var classifier = await SemanticCategoryClassifier.TrainAsync(embedder, examples, _localAi.Neighbours, ct);
            if (classifier is null)
            {
                return none;
            }

            var texts = uncategorized.Select(SemanticTraining.TextOf).Take(_localAi.MaxTextsPerRequest).ToList();
            return await SemanticTraining.RankAsync(embedder, classifier, texts, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A broken/missing model must never break categorization: fall back to the keyword + naive-Bayes steps.
            return none;
        }
    }

    public static string PromptText(string? s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in s ?? string.Empty)
        {
            sb.Append(char.IsControl(ch) ? ' ' : ch);
            if (sb.Length >= 80)
            {
                break;
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>Asks the language model to pick among the embedder's candidates (or the user's popular categories). Null = none/unavailable.</summary>
    private async Task<string?> AskLlmAsync(
        Transaction t, IReadOnlyList<Prediction>? candidates, List<string> popular, IReadOnlyDictionary<string, List<string>> shots, Prediction? current, CancellationToken ct)
    {
        var choices = (candidates?.Select(c => c.Label) ?? [])
            .Concat(current is null ? [] : [current.Label])
            .Concat(popular)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        if (choices.Count == 0)
        {
            return null;
        }

        // Bank text is untrusted: strip control characters/newlines and cap length so a crafted descriptor cannot add prompt lines.
        var subject = PromptText(string.IsNullOrWhiteSpace(t.MerchantName) ? t.Description : t.MerchantName + " / " + t.Description);
        var examples = string.Concat(choices
            .SelectMany(c => shots.GetValueOrDefault(c) ?? [], (c, text) => $"- {PromptText(text)} => {PromptText(c)}\n"));
        var user = (examples.Length == 0 ? string.Empty : "Labelled examples from this user:\n" + examples)
            + $"Transaction: {subject}\nDirection: {t.Direction}\nAmount: {t.Amount.Amount:0.00}\nCategories:\n"
            + string.Join("\n", choices.Select((c, i) => $"{i + 1}. {PromptText(c)}")) + "\n0. none of these\nAnswer:";
        try
        {
            var pick = await llm!.ChooseAsync(
                "You classify bank transactions into one of the user's categories. Reply with only the number of the best category; 0 means none of them fits.",
                user, choices, ct);
            return pick is { } i ? choices[i] : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Sets one transaction's category (a manual choice, never overwritten later). With <paramref name="applyToMerchant"/>
    /// every other transaction of the same merchant that is uncategorized OR was categorized automatically gets it too
    /// (they become Auto, so a later correction can still replace them); categories the user chose by hand are left alone.
    /// </summary>
    public async Task<int> SetCategoryAsync(Guid transactionId, Guid categoryId, bool applyToMerchant, CancellationToken ct = default, uint? expectedVersion = null)
    {
        var target = await store.FindTrackedAsync(transactionId, ct)
            ?? throw new KeyNotFoundException("Transaction not found.");
        guard?.Expect(target, expectedVersion);
        // The id comes from the request body: it must be one of THIS tenant's categories (the repository is tenant-scoped).
        var category = (await categories.ListAsync(ct)).FirstOrDefault(c => c.Id == categoryId)
            ?? throw new KeyNotFoundException("Category not found.");
        var id = category.CategoryId;
        target.Categorize(id);
        var changed = 1;

        if (applyToMerchant)
        {
            var key = AutoCategorizer.MerchantKey(target.MerchantName, target.Description);
            var candidates = await store.LoadTrackedUncategorizedOrAutoAsync(Max, ct);
            foreach (var t in candidates.Where(t => t.Id != target.Id
                         && (t.CategoryId is null || t.CategorySource == CategorySource.Auto)
                         && t.Status.IsReal()
                         && AutoCategorizer.MerchantKey(t.MerchantName, t.Description) == key))
            {
                t.Categorize(id, null, CategorySource.Auto);
                changed++;
            }
        }

        await store.SaveAsync(ct);
        return changed;
    }

    /// <summary>
    /// Re-derives the auto-assigned "Transfer" categories after an account's type changed (card/loan payments become
    /// transfers, or stop being them). Only auto categories that disagree with the current transfer detection are
    /// cleared (an auto Transfer that is no longer a transfer; a detected transfer leg carrying another auto category),
    /// then <see cref="RunAsync"/> re-categorizes them. Manual categories are never touched. Returns rows reset.
    /// </summary>
    public async Task<int> RecategorizeAfterAccountTypeChangeAsync(CancellationToken ct = default)
    {
        var txs = await store.LoadAsync(Max, ct);
        var real = txs.Where(t => TransactionStatusExtensions.IsReal(t.Status)).ToList();
        var accountTypes = (await accounts.ListAsync(ct)).ToDictionary(a => a.Id, a => a.AccountType.ToString());
        var transferIds = TransferDetector.Detect(real.Select(TransactionMapper.ToDto).ToList(), accountTypes);
        var transferCategoryId = (await categories.ListAsync(ct))
            .FirstOrDefault(c => string.Equals(c.Path.Value, AutoCategorizer.TransferCategory, StringComparison.OrdinalIgnoreCase))?.Id;

        var reset = 0;
        foreach (var t in real.Where(t => t.CategorySource == CategorySource.Auto && t.CategoryId is not null))
        {
            var isTransferCategory = transferCategoryId is not null && t.CategoryId!.Value.Value == transferCategoryId;
            if (isTransferCategory != transferIds.Contains(t.Id) && t.ClearAutoCategory())
            {
                reset++;
            }
        }

        if (reset > 0)
        {
            await store.SaveAsync(ct);
            await RunAsync(ct);
        }

        return reset;
    }

    /// <summary>Clears every category the auto-categorizer set (manual categories are untouched) so it can be re-run with better rules.</summary>
    public async Task<int> ClearAutoCategoriesAsync(CancellationToken ct = default)
    {
        var txs = await store.LoadTrackedAutoCategorizedAsync(Max, ct);
        var cleared = txs.Count(t => t.ClearAutoCategory());
        await store.SaveAsync(ct);
        return cleared;
    }
}
