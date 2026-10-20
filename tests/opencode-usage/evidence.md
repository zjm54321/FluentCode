# Phase 2 lane A — offline usage evidence

## P2 ambiguous timestamp fix (2026-10-10)

Command (from WSL, Windows .NET 10 direct csc harness):

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'C:\Users\Zhang\source\repos\AIFrontend\tests\opencode-usage\run.ps1'
```

- Final exit code **0**; `Headless compilation passed (warnings treated as errors).` Nullable enabled, no restore.
- Exact result: `PASS: 3 usage groups, 110 assertions; production-linked; fake HTTP only; no live service/model/config/ACP.` All original 90 assertions retained; 20 targeted assertions added.
- Full final log: `/tmp/opencode/aifrontend-p2-boundary-regression.log`.
- Compiler response file, executable and `results.txt`: `C:\Users\Zhang\AppData\Local\Temp\opencode-usage-1ff947b03f1741449d0f17c66ba1025b`.
- Scoped pre-edit originals: `/tmp/opencode/aifrontend-p2-boundary-originals-dbcaTj` (estimator, test program, this evidence file; original relative paths preserved).
- Policy: only finite, strictly positive timestamps establish ordering. The frozen nonnullable `Created` DTO maps omitted `time.created` to 0, so explicit epoch 0 and default 0 are conservatively indistinguishable. Compaction/model-switch timestamps and optional caller `compactionBoundary=0` produce `UnknownBoundary` + `Stale`; zero-dated assistant usage cannot become an anchor, and remains an unknown boundary even alongside a valid positive-time anchor. No guessed timestamps or DTO/transport/global token validator changes; valid zero token counts remain accepted.
- Regression fixtures actually deserialize JSON `time:{}` and `time:{"created":0}` for compaction, model-switched and assistant messages. Boundary cases retain a valid positive assistant/current model, matching model limit and complete history. Positive controls retain earlier/later compaction/model-switch ordering, earlier/equal/later caller boundaries and fresh zero-token usage.
- First harness attempt reported `CS9007` in the new interpolated raw JSON fixture; JSON closing-brace whitespace was corrected, then the same permitted harness passed as above.
- Public signatures unchanged. Only `src/AIFrontend/Services/OpenCode/OpenCodeContextEstimate.cs`, `tests/opencode-usage/Program.cs`, and `tests/opencode-usage/evidence.md` edited. No full app build, real requests, other lane edits, commits or resets; parent/oracle own closure.

## Result (2026-10-09)

Command:

```powershell
pwsh -NoProfile -File tests/opencode-usage/run.ps1
```

- Windows .NET 10 SDK 10.0.401; direct Roslyn csc, nullable enabled, warnings as errors; no restore/new packages.
- PASS: **3 groups, 90 assertions**, explicitly linking production OpenCode sources. HTTP uses only injected `HttpMessageHandler` with synthetic fixtures at `https://offline.invalid/prefix`.
- Full run log: `/tmp/opencode/aifrontend-phase2-usage.log`.
- Compiler response file, executable and `results.txt`: `C:\Users\Zhang\AppData\Local\Temp\opencode-usage-6f9bc457619647bca0e4bde311c96012`.
- Earlier initial run passed 88 assertions; final run additionally covers missing root wire cost and independent/repeated client reads (no shared usage cache).
- No real services/models, config, ACP, Billion, UI, package/lock changes, commits or resets. Existing files outside this lane were not edited.

## Coverage

1. Exact-parent recursive traversal, root/child/grandchild unique accounting, duplicate rows and conflicting cycle samples, repeated full pages with changing cursors, repeated cursor with distinct data, opaque query encoding, short-page continuation, empty-page termination, fork/wrong-parent exclusion, independent-branch read failure, missing/malformed required fields, root read failure, global session/page budgets, invalid cursors, caller cancellation and no cross-client cache.
2. Root and total Session.Info costs plus per-session details; five disjoint token categories (noncached input, visible output, reasoning, cache read, cache write); invalid/overflow numbers marked incomplete; zero never proves free; no external ACP/Billion total merging. Model list deepObject directory query, model limits, same-model/same-array-tier absolute-price ratios, missing cache prices, zero/nonfinite input denominator. No message-level pricing enters aggregation.
3. Latest successful assistant usage only; never session cumulative tokens or compaction usage. Current matching model context limit; always a reference, never an exact count. Model/provider/variant changes, switch-away-and-back evidence, compaction boundary/message, missing boundary dates and incomplete history mark stale; fresh post-compaction assistant usage restores the reference.

## Added production API

```csharp
Task<OpenCodeSessionsResponse> OpenCodeClient.ListChildSessionsForUsageAsync(
    string parentId, int limit, string? cursor = null, CancellationToken cancellationToken = default);
Task<OpenCodeModelsResponse> OpenCodeClient.ListModelsAsync(
    string directory, CancellationToken cancellationToken = default);
Task<OpenCodeUsageSnapshot> OpenCodeUsageService.ReadTreeAsync(
    OpenCodeClient client, string rootSessionId, OpenCodeUsageBudget? budget = null,
    CancellationToken cancellationToken = default);
UsageTreeSummary OpenCodeUsageAggregator.Aggregate(
    string rootSessionId, IEnumerable<OpenCodeSessionInfo> sessions,
    IEnumerable<OpenCodeUsageIssue> incompleteReasons,
    OpenCodeDerivedCacheMultipliers? referenceCacheMultipliers = null);
OpenCodeDerivedCacheMultipliers? OpenCodeUsageAggregator.DeriveCacheMultipliers(
    OpenCodeModelInfo model, int costIndex);
OpenCodeContextEstimateResult OpenCodeContextEstimate.Estimate(
    string sessionId, IEnumerable<OpenCodeMessage> messages, OpenCodeModelRef? currentModel,
    OpenCodeModelInfo? modelInfo, bool historyComplete, double? compactionBoundary = null);
```

- `UsageTreeSummary`: root/total cost, five-category tokens, immutable per-session details and incomplete reasons; optional reference cache multiplier carries provider/model/cost-index/tier. Its fixed label is **Core 累计估算**. Totals are known partial amounts when incomplete; no current-model repricing of historical costs.
- `OpenCodeUsageBudget`: defaults 512 sessions (including root), 1024 pages total (including empty terminal pages), page size 50 (accepted 1–500).
- `OpenCodeUsageSnapshot`: sampling start/end, summary. **Caller owns source/revision binding and late-result rejection**, using the correct client and matching messages/model metadata. No App/WinUI dependency.
- Transport uses a stricter usage-only root/list read so missing wire numeric fields cannot silently become valid zero through the existing nonnullable session DTOs. The shared client/DTO semantics are unchanged.
- Model wire shape checked against the already-local public-contract snapshot `/tmp/opencode/v2-openapi-session-check.json`: model response `{location,data}`, costs array with optional `{type:"context",size}` tier, prices per million tokens. No external fetch was performed. The snapshot is not asserted to be uniquely tied to core 2.0.24; accounting semantics follow the approved lane documents.

## Boundaries / not covered

- No UI integration/full Windows app build (parent owns review/build; designer owns UI).
- Session tree reads are not an atomic server snapshot; concurrent mutation may invalidate an otherwise complete traversal. No automatic cache, background refresh, source/revision manager or stale timer was added.
- Existing public DTOs supplied directly to the pure aggregator cannot distinguish omitted numeric fields from explicitly supplied zero; the usage transport validates presence before deserializing.
- Model catalog does not establish historical pricing coverage, external bills or whether zero is free. A multiplier is for one explicitly selected current catalog tier only; no automatic tier selection from cumulative tokens.
- Context consumes already-obtained same-session messages and caller boundary/completeness evidence; it does not fetch transcript/context, estimate subsequent text, or provide a current occupancy percentage. Unknown/missing history must be passed as incomplete.
- No live-service contract compatibility test, HTTP body-size cap, elapsed whole-tree deadline (per-request timeout plus cancellation/budgets apply), or exhaustive malformed-model payload matrix.
