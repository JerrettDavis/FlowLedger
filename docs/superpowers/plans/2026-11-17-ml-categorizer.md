# ML Categorizer Implementation Plan

> **For agentic workers:** use superpowers:executing-plans. Steps use checkboxes.

**Goal:** Local naive-Bayes categorizer that fills still-uncategorized transactions, with confidence and one-click suggestions.
**Architecture:** Pure `NaiveBayesCategoryPredictor` in Application; wired as final step of `CategorizationService.RunAsync`; three nullable Transaction columns; accept-suggestion endpoint; Web chip.
**Tech Stack:** .NET, EF Core/Postgres, Blazor.
**Spec:** docs/superpowers/specs/2026-11-17-ml-categorizer-design.md

## Global Constraints
- Threshold default 0.80 (`Categorization:MlThreshold`); suggestion floor 0.35; cold-start: >= 20 Manual labels, categories need >= 2 examples.
- Never overwrite Manual; Transfer excluded from training; no new packages.

## Review Focus
- Empty/whitespace merchant+description -> no prediction, no crash.
- Single-category training set -> no prediction (can't discriminate).
- Credit vs debit with same merchant text -> direction feature separates.
- Manual re-categorize clears suggestion and confidence.

### Task 1: Predictor
**Files:** Create `Application/Features/Categories/NaiveBayesCategoryPredictor.cs`; Test `tests/FlowLedger.Application.Tests/.../NaiveBayesCategoryPredictorTests.cs`
**Interfaces:** Produces `record TrainingExample(string Merchant, string Description, string Direction, decimal Amount, string Label)`; `record Prediction(string Label, double Probability)`; `NaiveBayesCategoryPredictor.Train(IEnumerable<TrainingExample>)` -> instance (or null if cold-start) with `Prediction? Predict(string merchant, string description, string direction, decimal amount)`; static `CrossValidate(examples, folds, threshold)` -> `(double Coverage, double PrecisionAtThreshold)`.
- [ ] Failing tests: separates two merchants; blank text returns null; <20 examples returns null; direction splits same text.
- [ ] Implement (tokens + char 3-grams, Laplace smoothing, softmax-normalized log-probs).
- [ ] Tests pass.

### Task 2: Domain + persistence
**Files:** Modify `Domain/Aggregates/Transaction.cs`, `Infrastructure/.../TransactionConfiguration.cs`; new migration `AddTransactionCategorySuggestion`.
**Produces:** `double? CategoryConfidence`, `CategoryId? SuggestedCategoryId`, `double? SuggestedConfidence`; `Categorize(..., double? confidence = null)`; `SetSuggestion(CategoryId?, double?)`. `Categorize` and `ClearAutoCategory` clear suggestion.
- [ ] Domain tests for set/clear behavior. [ ] Implement + migration. [ ] Build.

### Task 3: Pipeline
**Files:** Modify `CategorizationService.cs`; options `CategorizationOptions`.
- [ ] Test (fake store): confident -> Auto + confidence; mid -> suggestion only; Manual untouched. [ ] Implement; result record gains `ByModel`, `Suggested`.

### Task 4: API + DTO + Web
**Files:** `TransactionDtos.cs`, `CategorizationEndpoints.cs`, `Web/ApiClient/ApiModels.cs`, `CategoryChip`/SpendingPage.
**Produces:** `POST /api/transactions/{id}/accept-suggestion`; DTO fields `SuggestedCategoryId/Path`, `SuggestedConfidence`, `CategoryConfidence`.
- [ ] Endpoint test. [ ] Implement. [ ] Chip with accept button + "ML n%" badge.

### Task 5: Deploy and verify
- [ ] Full tests; build; restart stack; run categorizer; report cross-validated precision at threshold; browser spot-check.
