# ADR 0013 — Local, in-process AI assistance (embeddings first, optional small LLM)

Status: accepted (2026-11-21)

## Context

FlowLedger already has a keyword categorizer, a naive-Bayes model trained on the user's own manual categories, and an Ollama
translator for the "Ask" page. Naive Bayes only knows words it has seen, so it cannot categorize a merchant it has never met
(measured ~60% precision at its best threshold on held-out merchants; auto-apply is therefore OFF by default, see
`CategorizationOptions`). "Same merchant" logic everywhere is an exact upper-cased string (`MerchantKey`), so
`STARBUCKS #1042` and `SQ *STARBUCKS` are different merchants.

Requirement: add model-based help **without any extra service or server** (no Ollama requirement, no cloud), keep every uncertain
decision adjustable, and never silently overwrite what the user chose.

## Decision

1. **Sentence embeddings are the workhorse, run in-process with ONNX Runtime** (`Microsoft.ML.OnnxRuntime`, CPU).
   A frozen embedder plus a non-parametric nearest-neighbour head ("SetFit-lite") needs no training run, no stored model and learns a
   correction the moment the user makes it (the classifier is rebuilt per request from manual categories).
   Used for: (a) category **suggestions** for unseen merchants, (b) **look-alike merchant grouping** (→ one-click aliases),
   (c) "similar transactions". An LLM is the wrong first tool for these: 100–1000x slower, non-deterministic, and no more accurate than a
   good embedder + the user's own labels.
2. **Default model: all-MiniLM-L6-v2, int8 (22M params, 384-d, ~23 MB).** Fastest, tiny, most widely deployed.
   Catalog alternatives (config switch `LocalAi:EmbeddingModel`): **bge-small-en-v1.5 int8** (33M, 384-d; MTEB English-v2 retrieval ≈
   53.9 vs ≈ 42.9 for MiniLM, ~1.5x the CPU cost). Considered next: snowflake-arctic-embed-xs (same size as MiniLM, retrieval 50.2 vs 42.0 on its
   model card), arctic-embed-s, gte-small, multilingual-e5-small (non-English descriptors). Adding one is a `ModelCatalog` entry.
3. **Zero-download fallback.** A dependency-free hashing embedder (word + char 3–5-gram feature hashing, 512-d) is used when no model is
   installed or it fails to load, so every feature works offline and tests are deterministic. The Local AI page and `/api/local-ai/status`
   say which embedder is *actually* active.
4. **Optional generative tie-breaker: LLamaSharp (llama.cpp) + a small GGUF**, opt-in (`LocalAi:Llm:Enabled`), only for transactions
   the embedder is unsure about, only choosing among the embedder's top candidates (+ "none") with a **grammar-constrained** answer,
   capped per run, **never in the sync hot path**. Candidate models: Qwen2.5-1.5B-Instruct Q4_K_M (default, ~1.1 GB, safe on any llama.cpp build),
   Qwen3-1.7B / Qwen3-4B-Instruct-2507 (best sub-4B classification in published small-model benchmarks), Llama-3.2-1B/3B, Gemma-3-1B/4B.
   The "Ask" translator **stays on Ollama (qwen3:8b)** by default; a 1–2B model would regress it. A provider switch is the touchpoint.
5. **Held-out evaluation is part of the feature.** `POST /api/local-ai/evaluate` runs group k-fold *by merchant* (the learned-merchant step already
   handles seen merchants, so unseen ones are the only case a model adds value) and compares naive Bayes, embedding-kNN and the ensemble, with a
   precision/coverage curve. Defaults stay conservative (suggest only); the user picks a threshold from their own numbers.
6. **Ensemble rule.** Agreement → noisy-OR of confidences; disagreement → the semantic answer wins unless naive Bayes is >0.25 more confident
   (Bayes probabilities are poorly calibrated). On a seeded dataset this took the ensemble from 57% to 64% overall hit rate (kNN alone 65%).
7. **`MerchantKey` is not changed.** Semantic grouping is suggest-only and produces ordinary merchant aliases.

## Security and operations

- Models are downloaded only on an explicit, household-owner-only, rate-limited action. The request carries a catalog **id, never a URL**; URLs in the
  catalog are pinned to a Hugging Face commit and every file is verified by size + SHA-256 before being moved into place. Hosts are allow-listed
  (including after redirects). Nothing downloads at startup. `LocalAi:AllowModelDownload=false` disables it; files can be copied in by hand.
- Model files live outside the repo (`LocalAi:ModelDirectory`, default `%LOCALAPPDATA%/FlowLedger/models`; mount a volume in containers).
- A native-load or model failure degrades to the fallback and is shown in status; it never breaks categorization or startup.
- Vector cache is bounded, in-memory, keyed by (kind, normalised text) per backend, never persisted. No migrations.

## Adjustable touchpoints (`LocalAi:*`)

`Enabled`, `Embedder` (Auto|Hashing|Onnx), `EmbeddingModel`, `ModelDirectory`, `AllowModelDownload`, `SemanticFloor`, `Neighbours`,
`SameMerchantThreshold`, `MaxTextsPerRequest`, `Llm:{Enabled,Model,MaxCallsPerRun,ContextSize,Threads}`; plus the existing
`Categorization:MlThreshold` (auto-apply, default off) and `Categorization:SuggestionFloor`.

## Consequences

- +~20 MB native/managed weight for ONNX Runtime; the optional LLamaSharp CPU backend adds ~100 MB of native binaries to hosts that reference Infrastructure.
- Accuracy numbers on the Local AI page are measured on the household's own labels; below ~30 labelled merchants it declines to score.
- Future: persist embeddings per merchant to skip recomputation; feed merchant groups into recurring-flow/bill-sheet matching; fine-tune a SetFit head if kNN plateaus.

## Sources consulted

Snowflake arctic-embed model card (MTEB retrieval comparison); 2026 embedding rankings (MiniLM / bge-small / arctic); small-language-model classification
benchmarks (Qwen3, Llama 3.2, Gemma 3); LLamaSharp 0.30 release notes (llama.cpp c0c7e14).

## Validation log (2026-11-22, seeded 66-merchant / 7-category set; re-run on real data before trusting)

- Held-out merchants, embedding kNN: **MiniLM 65% vs bge-small 61%**; ensemble 64% vs 61%. bge's cosine values sit higher, so the confidence mapping
  (tuned on MiniLM) over-covers at low thresholds; MiniLM stays the default and bge would need its own calibration constants before being preferred.
- LLM few-shot (user's labelled merchants in the prompt) made Qwen2.5-1.5B **worse** (Speedway to Utilities, Wawa to Medical): shipped as `LocalAi:Llm:FewShot`, default off.
  The 1.5B model is a weak adjudicator (roughly 2 of 7 right on unseen merchants); it stays suggest-only. Try Qwen3-4B before enabling few-shot.
- Hardening added: downloader tests (bad checksum, oversize body, redirect to a foreign host, HTTP errors, retry after failure), prompt-injection neutralisation
  (control characters stripped, 80-char cap), LLM load failure retries after 2 minutes instead of staying disabled until restart.

## Validation log (2026-11-23, real household data: verified 2026-11-23 snapshot, primary tenant, 176 hand-categorized merchants / 42 categories)

Run with `RealDataEvaluationTests` (opt-in via `FLOWLEDGER_REAL_SNAPSHOT`/`FLOWLEDGER_REAL_PG`/`FLOWLEDGER_REAL_OUT`; restores the snapshot to a scratch DB and calls the
app's own `EvaluateAsync`; emits aggregates only). Held-out merchants, overall hit rate, 5-fold / 10-fold:

| Method | hashing | MiniLM | bge-small |
|---|---|---|---|
| Naive Bayes (current) | 35% / 33% | 35% / 33% | 35% / 33% |
| Embedding kNN | 32% / 33% | 32% / 30% | 34% / 33% |
| Ensemble | 36% / 35% | 34% / 35% | 35% / 34% |

- **The seeded-set numbers did not transfer.** Naive Bayes is about 34% (not ~60%) and kNN about 30-34% (not 65%) on real unseen merchants. The seeded set was easier (clean, well-separated categories).
- bge-small does not lose to MiniLM here: higher kNN precision at the same threshold (about 82% at 0.5 with 21% coverage, versus about 55% for MiniLM at 0.5 with 33% coverage; MiniLM reaches 74% only at 0.7 with 15% coverage).
  Both are far from auto-apply quality, so `Categorization:MlThreshold` stays off and everything remains suggest-only. If suggestions are filtered, bge-small kNN at >= 0.5 is the best precision/coverage point measured.
- Ensemble is at or slightly above the best single method overall, and its precision at 0.9 is about 86% on bge (14% coverage) but only about 47% on MiniLM, so MiniLM's confidence mapping is the poorer-calibrated one on real data.
- Likely ceiling: fuzzy label boundaries (Dining / Snacks / Fast Food / Convenience) and overlapping categories (Food vs Food & Dining, Transport vs Auto & Transport, Fees vs Business/Fees). Consolidating those should raise measured accuracy more than a larger model.
