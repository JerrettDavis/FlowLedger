# ML categorizer (ML enhancement #1 of 5)

Roadmap, each shipped, deployed and tested before the next: (1) learned categorizer, (2) anomaly detection, (3) better recurring detection, (4) forecast uncertainty bands, (5) local embeddings/LLM for merchant names and NL queries.

## Intent
Keyword regexes and exact-merchant memory leave many transactions uncategorized. A small local model trained on the user's own manual corrections fills the gaps, with a confidence score, and learns from every correction. All processing stays in-process; no transaction data leaves the machine.

## Goals / non-goals / anti-goals
- Goals: categorize leftovers after transfer/learned-merchant/keyword steps; expose confidence; one-click accept of low-confidence suggestions.
- Non-goals: embeddings/LLMs (#5), changing keyword rules, touching rows with a Manual category.
- Anti-goals: silently mis-filing money. Never overwrite Manual; low confidence never auto-applies; no new native/runtime dependencies.

## Approaches
A. In-process multinomial naive Bayes / TF-IDF, retrained each run. **Chosen** (ms on thousands of rows, deterministic, Docker/test friendly, real probabilities).
B. ML.NET + persisted model: extra package, model-file lifecycle, retrain job; overkill at this size.
C. Embeddings/LLM: heavy; belongs to #5. Interface (`ICategoryPredictor`) stays swappable.

## Design
- `NaiveBayesCategoryPredictor` (Application/Features/Categories, pure, unit-tested). Features: word tokens + char 3-grams of merchant+description (normalized: lowercased, digits/store numbers stripped), direction, amount bucket (account type not used). Laplace smoothing; returns top category + normalized probability.
- Training set: rows with `CategorySource.Manual` and a category (excluding Transfer). Rows with fewer than ~20 total labels, or categories with <2 examples, are not predicted (cold-start guard).
- Pipeline: new final step in `CategorizationService.RunAsync` for rows still uncategorized. p >= `Categorization:MlThreshold` (default 1.01 = auto-apply OFF) -> `Categorize(..., Auto)` and store confidence. Otherwise p >= `Categorization:SuggestionFloor` (0.40) -> store suggestion only; category stays null.
- Domain: Transaction gains nullable `CategoryConfidence`, `SuggestedCategoryId`, `SuggestedConfidence` (EF migration; cleared on any Categorize/ClearAutoCategory).
- API: transaction/breakdown DTOs carry the suggestion; accepting reuses the existing recategorize endpoint (no new endpoint). Existing set-category corrections automatically become new training data next run.
- Web: Spending/transaction row shows a "Suggested: Food/Dining 62%" chip with accept button; auto-ML rows show a small "ML 91%" badge using the existing CategoryChip.
- Calibration (measured 2026-11-17, 231 real manual labels, 37 categories, 5-fold CV): raw naive Bayes was overconfident (63% precision at p>=0.8). Tempering by feature_count^0.5 fixes saturation, but the random-split numbers (92% at 0.70) were optimistic: they let a merchant appear in both train and test. Honest evaluation (folds grouped by merchant, fixed seed, only rows no keyword rule matches, i.e. what the model really sees) gives ~60% precision at p>=0.70, ~72% at 0.80, ~47% at 0.40. That is NOT safe to auto-apply, so v1 ships suggest-only; auto-apply is a config flip once measured precision justifies it. Follow-up: strip bank-noise tokens (DDA PIN POS PUR, card suffixes) and add more labels before revisiting. Known gap: category merge/move does not remap suggested_category_id (UI tolerates missing categories).
- Offline evaluation: a test/CLI path doing k-fold accuracy over Manual rows so the threshold is set from data (reported precision at threshold), not guessed.

## Constraints / builder calls
- Tenant-scoped: model trained per tenant per run; never cross-tenant.
- Failures never fail sync (matches existing try/catch pattern in `CategorizingSyncService`).
- Deploy: apply migration on stack restart, run categorizer, verify counts and precision on real data, spot-check in browser.
