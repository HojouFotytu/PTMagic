# PTMagic 2.10 — Cumulative release changes

## November 26, 2026 — Dev 17 lifecycle validation: obsolete startup SOM assertion

Operator execution at `df26022` compiled and passed the first 55 ConfigurationLifecycle checks, then failed at Program.cs:801. The test incorrectly required zero sends on inconclusive selection, a superseded pre-Dev 16 deferral rule. Corrected regression now exercises acknowledged provisional delivery, failure of a distinct conclusive payload preserving acknowledgement, and successful recovery with manual startup SOM unchanged. No production changes. Full rerun pending; do not record a suite PASS yet.

## November 26, 2026 — Dev 17: scope incomplete-data decisions to actual Global/freshness failure (native validation pending)

Verified prelisting history diagnostics now inspect the previous Delta calculation and its independent benchmark. Current-series continuity and exact listing metadata are still required; historical-only shortages cannot suppress current-measurement errors. Verified historical flat PricePathEfficiency is informational, without changing UNAVAILABLE truth or metric calculations. Regression cases cover historical flat paths, fresh in-flight watchdog recovery, definite Global after unknown candidate and fresh provisional SMS retention. Native tests remain unexecuted.

A definite ordinary Global match is not provisional solely because an earlier candidate's expression was unavailable. Preserve unknown inputs and metric diagnostics; if no definite match exists, existing best-available selection and optional FAILSAFE remain. Refresh the watchdog heartbeat on completed fresh observations even when classification is inconclusive. Serialize heartbeat and Global compilation with watchdog delivery, bypass flood protection on provisional recovery, and do not force fresh raids provisional because the watchdog delivered earlier in their lifetime. Recheck actual age at delivery. Fresh provisional classification without FAILSAFE evaluates SMS and preserves local overlays and held defense; stale analysis or actual FAILSAFE still suppresses them. Acknowledged reason and SMS-suppression state are persisted separately from quality and transport, with legacy fallback; Monitor no longer hides confirmed valid SMS solely because the Global is provisional. Raid summaries report the acknowledged incomplete-data reason. Native execution and live behavior remain unverified.

## November 26, 2026 — Consolidated source and validation checkpoint (documentation reconciliation)

**Current application source:** `24db0b56bd7c33e32fcccfc25f6aaf40a1883bd5` on `phase1/market-data-foundation`. Source changes listed below are not evidence of deployed binaries. Latest FAILSAFE/watchdog, Global description, Monitor layout, logging/export and current data/held-defense extensions need new native solution build, focused tests, TestMode and live acceptance; do not claim that earlier passing tests validate this head.

**Observed local validation from operator (earlier checkpoints, not full-head certification):** DecisionHistory **17/17 PASS** after path normalization plus **PTMagic.sln build PASS** (Core/PTMagic/Monitor, November 24); PTDataExport **28/28 PASS** after numeric day/week/month ZIP selectors (November 25). ConfigurationLifecycle FAILSAFE and MonitorSettingsRoundTrip changes were published afterward with **no execution result reported**. Earlier UI and download behavior changes remain browser-acceptance pending.

**Monitor corrections missing from prior notes:** Control menu now reads `PTMagic Logs/Data` above `PT Data` (route remains `Logs`); PT Data page's unnecessary non-upload disclaimer was removed. Application Settings storage estimate labels shortened to `Estimated space required`, `Available`, `Observed`. A failed *attempted* `Configuration delivery` raid-summary line uses WARN; a successful provisional acknowledgement uses WARN with `(Incomplete Data)` for both confirmed setting and delivery. Global and SMS ticker text have no tooltips; only respective `(i)` icons do, and a saved optional Global `Description` replaces repetition of the name in the Global icon. No LONG/SHORT distribution variants exist: the development and packaged default Analyzer templates are two generic copies.

**Current unresolved checks:** complete build and regression suite at `24db0b5`; confirm optional FAILSAFE removal and best-available selection; confirm independent deadline watchdog, failed-raid recovery, multi-target acknowledgements, SMS overlay clearing and restoration, persisted `IncompleteData` after restart, missed data sources and runtime color changes; verify native sell-only enforcement cannot be bypassed by pair-level rules. The watchdog's absent-FAILSAFE path may use the latest retained classification when no new observation has arrived; do not imply a new market measurement was performed. The provisional candidate ranking counts known positive clauses and is a best-effort heuristic, **not** validated market inference or a guarantee of safer outcomes.

## November 26, 2026 — Optional FAILSAFE and incomplete-data Global delivery (source committed; native validation pending)

Replaced indefinite stale/inconclusive Global delivery deferral with an explicitly marked provisional policy. The reserved case-insensitive Global SettingName `FAILSAFE` is optional and is excluded from ordinary metric-trigger classification. When current analysis is incomplete, inconclusive or stale, configured FAILSAFE supersedes the ordinary Global and clears SMS overrides. Native DEFAULT sell-only is explicitly emitted even if the DEFAULT preset omitted the key. Without FAILSAFE, retain normal metric truth semantics, evaluate every valid available input, choose a conclusive Global when possible or the best-supported configured Global (known positive trigger evidence and original priority), and otherwise use the latest known classification/default as the only remaining evidence; do not substitute fabricated zero measurements. The same mechanism applies to either trading side.

An independent five-second deadline watchdog can publish a provisional complete configuration without waiting for an overdue raid. It builds detached property lines from configured presets, serializes external delivery with normal raids, guards against a late raid overwriting its selection, honors startup SOM uncertainty, and avoids repetitive unchanged provisional sends. A failed raid invokes the same recovery attempt immediately, subject to retry throttling. PT delivery still requires full acknowledgement, and failed/partial transfers leave the prior confirmed state unchanged. On fresh, conclusive evidence, successful normal delivery restores ordinary Global/SMS behavior.

Acknowledged quality is persisted in the existing delivery checkpoint as `IncompleteData` and exposed in the Monitor runtime summary as `ConfirmedIncompleteData`. Raid-summary `Confirmed PT setting` and `Configuration delivery`, Monitor Status Summary and ticker health use warning color and the `(Incomplete Data)` suffix while the acknowledged configuration is provisional; this is distinct from transport failure. The distribution's TWO analyzer template files (`_Development/DevSettings/settings.analyzer.json` and `PTMagic/_defaults/_default_settings_PT_2.x/settings.analyzer.json`) now each contain ONE optional FAILSAFE Global with only `DEFAULT_sell_only_mode_enabled=true` and detailed comments. These are not separate LONG/SHORT distributions. No LONG/SHORT strategy settings repository files or default formulas were changed. The sample makes no claim to reset native pending-entry trails or tighten structural exits.

ConfigurationLifecycle regressions added/updated for missing-metric fallback, stale provisional delivery, FAILSAFE override, SMS removal, watchdog pre-raid delivery, normal recovery, persistent quality, failure rollback and the absent-FAILSAFE behavior. Distribution templates passed static JSON-with-comments parsing. .NET build, all focused tests and deployment/runtime behavior remain UNVERIFIED; source publication is not proof of live PT protection. Notes for acceptance: confirm native sell-only setting, multi-target complete acknowledgement, watchdog action at the actual deadline, and exchange-order/armed-entry behavior.



## November 26, 2026 — Global description and ticker tooltip simplification (validation pending)

Add optional `Description` to the GlobalSetting schema and Monitor Analyzer editor, including the Default setting. The editor now persists, clears and roundtrips this field without changing trigger or configuration behavior. In Monitor's ticker, neither the Global setting name nor the SMS text/count has its own tooltip; only the existing information icons display those details. The Global information icon uses the selected Global's HTML-escaped description, if present, instead of repeating its setting name, while preserving instance name, PT delivery acknowledgement and startup SOM status. SMS information remains unchanged. Added editor roundtrip regression coverage. Native .NET build, regression execution and live ticker validation pending.

## November 26, 2026 — Contextual comparisons and graded single-market defense

Analyzer rules can compare the magnitude of a reversal to its preceding leg and to volatility observed before the move, and compare leader overlap to the actual incidence of origin leaders, including ties. Historical percentile metrics compare current market or contract data with 24 preceding comparable windows. Buyer and seller pressure can be compared directly. Participation intensity uses the amount of volume expansion or buying imbalance for each contract.

New funding metrics compare regular settlements with each contract’s preceding history using observed payment intervals. A separate signed carry estimate counts payments expected during the next trend horizon at the latest observed regular cadence and rate, then divides by the contract’s pre-move ATR percentage. A missing settlement history, overdue schedule or unavailable range produces an unavailable estimate.

A terminal single-market setting can name its immediately following, independently confirmed fallback for a first encounter with unavailable severity evidence. Previously applied stronger protection survives indeterminate evidence. Configuration validation checks the relationship. Existing configurations can omit all new fields and metrics.

## November 26, 2026 — Measured market legs, liquid-cohort breadth and independent held-market defense

Analyzer presets can opt into `HeldMarketDefense` with a numeric `HeldMarketDefenseProperty` in PAIRS. Fresh market evidence can raise that property's compiled Global floor to 1 or 2 before single-market settings run. An unavailable rule retains previously acknowledged protection only while the evidence still permits that level. Failed delivery, destination changes and test mode retain the existing acknowledgement boundaries. Decision history records desired and confirmed levels.

The 5-minute futures engine adds breadth for frozen top-50, top-75 and top-100 turnover cohorts, asset and market returns in ATR units, asset return in excess of the market median in ATR units, and event-anchored drawdown/rally measurements. Measured price recovery, participation and overlap use one aligned basket, shared turning points and only closed candles. For shock detection, MedianReturnInPriorATR divides the rolling market median return by median ATRP calculated as of the start of that return window. The developing shock cannot increase its own denominator. Configurations request each metric by name. Existing presets can omit the new controls.


## November 25, 2026 — Configurable PT Data and PTMagic log ZIP periods (validation pending)

Add a numeric count and days/weeks/months selector to PT Data trade-history exports and PTMagic/Monitor log ZIP downloads. Both support All available, including the existing all-history PT export default; calendar months use AddMonths. Server-side validation prevents invalid durations. PT Data retains original pagination, history manifests, source payloads, current snapshots and funding behavior, with a new shared parser accepting legacy range codes. The log ZIP filters complete dated daily log files (including the boundary day), with modification-time fallback for undated filenames, without filtering the live log viewer or altering the complete _data ZIP download. Regression tests cover range arithmetic, invalid input, legacy range codes, and dated/undated log selection. Native build, regression execution and browser verification pending.

## November 24, 2026 — Normalize settings base path for migration regression (retest pending)

Fix PTMagicConfiguration constructors to normalize base path separators once, ensuring WriteGeneralSettings and WriteAnalyzerSettings create backups and write files inside the requested instance directory even when the caller omits the trailing path separator. A real Windows .NET build succeeded and the first ten DecisionHistory regression checks passed; the following migration test exposed this save-path error. Extend the migration fixture to verify both backup paths and no accidentally concatenated sibling filenames. No Analyzer logic, retention policy or settings data are changed. Native regression rerun pending.


## November 24, 2026 — Application history retention UI and combined capacity projection (validation pending)

General Settings / APPLICATION now edits Application.HistoryRetentionDays (1–3650) with server-side pre-save validation. Displays ONLY combined estimated storage required, available disk capacity, and observed daily storage growth for MarketData and DecisionHistory; no dataset breakdown, no OHLCV included. A Core history estimator scans file metadata in the last 48 hours with a five-minute Monitor cache; browser recomputes projections as the requested days change, with no further disk scans. No source-data historical OHLCV archive is added in this change. Warns when estimated incremental growth exceeds available free space. Under one hour of observations reports "Collecting data" rather than claiming a precise estimate. A real filesystem regression covers combined accounting and excludes expired files from the observed growth rate. Settings and trading behavior are otherwise unchanged. Native build and browser acceptance pending.


## November 24, 2026 — Unified history retention core (tests pending)

Add Application.HistoryRetentionDays (default 30) for legacy MarketData snapshots and DecisionHistory. Old DecisionHistoryRetentionDays is automatically migrated when the new key is missing. Remove the separate DecisionHistoryMaxMegabytes limit and size-based pruning; preserve age expiration and the 16 MiB record/segment integrity bound. MarketData cleanup is scoped to MarketData_*.json and preserves any legacy active StoreDataMaxHours window. OHLCV cache, metric calculation, market acquisition, PT delivery and trading settings are unchanged. Add focused migration/retention/cleanup regressions. Monitor UI and combined storage estimate are addressed in the following change. .NET validation pending.


## November 23, 2026 — Condense new-market history INFO to one line (validation pending)

Replace the per-market INFO notices and recovery chatter with one sorted list: `+ New markets with insufficient history, excluded from some metrics: ...`. No per-market listing times, metric counts, or repeated explanatory text. Unavailable metric values, verified pre-listing classifications, genuine warning categories, market-data collection and trading behavior are unchanged. Updated Phase1MetricWarnings regression expectations for one market, six markets, repeated raids and no exclusions. Source published; focused build/tests and configured-runtime checks pending.


## November 23, 2026 — Remove residual normalized candle-refresh progress dots (validation pending)

Build 112320260217 still printed market-progress periods after prior dot removal because `MarketDataService.RefreshAtAsync` retained a third, independent per-market `Console.Write('.')` path for Binance Futures 5m normalized candle refresh. Removed that whole console-only prefix/dot/newline lifecycle, including its progress lock and surrounding completion-only try/finally. Existing `RAID TIMING - normalized 5m refresh` and `BinanceFutures analytics - normalized 5m data refreshed` INFO status remain, with unchanged request, cancellation, caching, status, source-quality and trading logic. The new real-path NormalizedWarmStartCache test asserts no stdout and successful 2-market refresh. **Code committed, .NET build and regression execution pending.**

## November 23, 2026 — Remove progress dots completely (validation pending)

Per owner direction, delete incremental period output from first-seen-date scans and SMS evaluation. Do not print per-market console fragments or attempt PM2/Monitor incremental-line rendering. Each operation emits a single normal INFO completion message (or interrupted first-seen status) containing the actual processed and active counts. This INFO record now appears in regular PTMagic file logs as well as the console, rather than producing dot-only PM2 lines. Remove redundant completion DEBUG records. Update ConfigurationLifecycle normal/cancelled and active/inactive regression expectations accordingly. **No trading decisions, market acquisition logic, Analyzer settings, Monitor reader, or PT API behavior changed. .NET build/runtime validation pending.**


## November 23, 2026 — Inline metric DEBUG and Monitor Logs SSE response completion (validation pending)

Removed end-of-raid flat-path DEBUG replay and emitted exact `PricePathEfficiency` source DEBUG with its MarketTrend calculation, and affected SMS caller DEBUG during the corresponding trigger evaluation. Only the compact INFO aggregate remains at RAID SUMMARY. Existing NO_TRADES DEBUG moves to the MarketTrend evaluation stage. The live first-seen-market and SMS Console.Write dot streams are **unchanged**; no buffered-at-completion substitution was made.

Monitor's `Logs?handler=Stream` handler previously returned `Task` after writing SSE chunks, allowing Razor Pages to attempt a second HTML page result on an already-started response and emit `Headers are read-only, response has already started`. It now returns `Task<IActionResult>` and `EmptyResult` after streaming, preserving 403/400 handling and SSE event behavior. A focused Monitor return-type regression is added. The supplied stack-trace tail does not by itself establish whether any other upstream exception is present.

**Published code only; new .NET build, focused suites and live Monitor/SSE runtime acceptance pending.** No trading, Analyzer or SMS JSON changes.


## November 23, 2026 — Expected flat-price-path efficiency is INFO, full trigger provenance in DEBUG (validation pending)

The raid's prior WARN included a full multi-SMS caller dump for source-verified `H1 PricePathEfficiency` flat 0/0 paths, even though this is a mathematically correct UNAVAILABLE result. Change **reporting only**: all-flat, nonstale source-valid asset-path groups emit a short INFO line such as `+ H1 PricePathEfficiency: 4/729 unavailable (flat price paths; 10 SMS trigger evaluations).`, while DEBUG retains all affected markets and distinct SMS trigger callers with context. The prior five-example WARN cap no longer discards caller identity from the DEBUG record. The canonical source reason is now a shared constant.

A group with stale/missing candles, mixed causes, a global error or unsupported metric stays WARN. Per-market trading-protection warnings remain WARN, as do delivery deferrals, without changing source validity, SMS decisions, PT settings, or acknowledgements. Added focused cases for 4/729, ten callers, reset and mixed/stale evidence. **Source published; .NET build, focused tests and live configured-log acceptance pending.** No Analyzer JSON or strategy settings were changed.


## November 23, 2026 — Per-market SMS protection unblocks conclusive global PT delivery (validation pending)

Previously, a few flat price paths produced mathematically unavailable asset-SMS `PricePathEfficiency` values. This made the first PT configuration permanently Deferred even though global selection and other markets were valid. PTMagic now preserves missing-data UNAVAILABLE while restricting only affected symbols with `PAIR_sell_only_mode_enabled = true` and `PAIR_DCA_enabled = false` in the compiled PAIRS SMS overlay. The temporary restrictions override conflicting known SMS pair permissions, preserve previously active SMS and unknown OffTrigger latches, and expire when the market returns to conclusive data. Unknown stop-process SMS also prevent lower-priority SMS evaluation on that market. A grouped warning identifies protected symbols.

Conclusive GlobalSettings and unaffected SMS may now proceed through unchanged full-payload PT acknowledgements. Global uncertainty, startup SOM uncertainty, stale source pins, TestMode and transport failures remain fail-closed. Added focused lifecycle regressions. **Source committed, but new .NET build, focused tests and deployed PT runtime verification remain pending.** No analyzer or preset settings modified.


## November 23, 2026 — Concentration available-market ranking with 90% coverage (validation pending)

The owner approved replacing Concentration's 100% complete ranked-population restriction with the standard **≥90% of the frozen eligible markets**. Markets without source-valid, aligned return or full quote-volume windows are excluded. Rank only valid markets by actual quote volume, take the highest-volume ten and subtract the remaining valid markets' mean return. Require at least eleven usable ranked markets, retain the original eligible denominator and do not invent ranks/values for missing markets. A 726/729 valid population is sufficient. The separate HHI/volume-share metrics retain their own coverage requirements. A new source-level regression verifies 11/12 valid and rejects 11/13 (<90%), including a high-volume symbol with missing return reference. **Build and owner-run test execution pending.** No Analyzer JSON or trading preset changed.


## November 23, 2026 — Linux state-lock startup and inline first-seen progress corrections (validation pending)

**Startup fault:** PTMagic could terminate in its constructor on Linux before its first raid when creating named `analyzerStateMutex` required missing `/run/user/1000`. Replace the sole state-write mutex with an exclusive, persistent file lock at `_data/AnalyzerState.lock`, held while writing the unchanged `_data/AnalyzerState` integer. FileShare.None preserves cross-process exclusion among instances running the new code over the same directory; bounded retries replace unbounded named-mutex wait. Do not delete the lock sentinel. The old and new lock protocols are different, so concurrent mixed-version writers sharing an `_data` directory are **not** coordinated; stop the old process before upgrading. No other PT settings, SOM, metric or trading logic is altered.

**First-seen display:** `BinanceAnalyzerCommon.CheckFirstSeenDatesAsync` now outputs its prefix, one period per checked market, and its completion on **one console line**. The old NLog INFO prefix was inserting a newline ahead of `Console.Write('.')`. One DEBUG file-log summary is retained, and cancellation/failure closes the progress line with an interrupted result.

**Focused tests added:** `ConfigurationLifecycle` covers state writing, exclusive-lock contention/release, separate data directories, zero-network first-seen progress, and canceled progress. Source committed, **.NET build, focused regression and deployed Linux PM2 runtime acceptance pending**. No existing diagnostic Analyzer or SMS fixture changes.


## November 22, 2026 — GlobalSetting Delta relation call-site fix; runtime acceptance pending

**Observed problem in v2.6.3 LONG/SHORT TestMode:** G10 \`DIAG_G10_DELTA\` selected yet its recorded values were equal to absolute current Breadth, MedianPricePathEfficiency and MedianTakerBuyRatio rather than historical differences. Cause verified in \`Core/Main/PTMagic.cs\`: \`CheckGlobalSettingsTriggers()\` called the legacy no-relation \`TryResolveGlobal\` overload, defaulting to Absolute despite \`trigger.MarketTrendRelation="Delta"\`. SMS14 relation-aware Delta and OffTrigger were independently functional. **Fix:** pass \`trigger.MarketTrendRelation\` into the already existing relation-aware global resolver. No Delta engine algorithm/metric registry/market data change. Existing Absolute and other relation handling retained.

Added a synthetic production-path regression to \`Tests/ConfigurationLifecycle/Program.cs\` (current 55, prior 40, Delta 15; Absolute 55; mismatch and unavailable history). Sole fixture \`Tests/Settings/settings.analyzer.json\` now **v2.6.4**, \`DescriptiveName="2.6.4"\` and G10 first; H1 MedianTakerBuyRatio Delta's diagnostic range [-0.25,0.25) deliberately rejects the previously observed erroneous ~0.517 absolute result while admitting the observed ~-0.0096 difference. This is a **test discriminant, not a regime decision threshold**. No other diagnostic settings, preset assignments or live settings changed.

**Current revision:** source and fixture changes committed; .NET build, new focused test execution and new LONG/SHORT TestMode acceptance remain **UNVERIFIED**.

## November 22, 2026 — Analyzer identity simplified to version-only (v2.6.3 fixture)

The single TestMode analyzer now specifies `MarketAnalyzer.DescriptiveName: "2.6.3"`, matching its separately maintained `SetVersion = 2.6.3` header. The raid heading is `2.6.3 RAID SUMMARY #N`, or `RAID SUMMARY #N` when blank. This correction removes the unnecessary `PTM 2.10 Delta` prefix; **no runtime heading, parser, source-calculation, trigger or trading behavior is changed**. Monitor's optional field example and focused editor/banner tests now use version-only labels. Build and live TestMode evidence for the updated revision remain pending.

## November 22, 2026 — Versioned descriptive name in raid summary banner (build/runtime pending)

Added an optional `MarketAnalyzer.DescriptiveName` field at the top of Monitor Analyzer settings, persisted by the existing analyzer JSON/form model. The log banner is `NAME RAID SUMMARY #N` when populated, or `RAID SUMMARY #N` when blank/missing. Existing live and historical Logs page readers rely on the exact `RAID SUMMARY` marker, **which is unchanged**. Long names widen the banner without truncation. No log reader, stream, export, summary file, decision-history schema, metric calculation, trading settings, or order-execution behavior changed.

The **single** testing analyzer `Tests/Settings/settings.analyzer.json` is now v2.6.2 with `DescriptiveName: PTM 2.10 Delta v2.6.2`. All Delta and prior diagnostic triggers, MarketTrends, preset references and earlier priority remain unchanged. Monitor settings roundtrip and banner formatting checks added. New source build and LONG/SHORT TestMode runtime acceptance are pending.

## November 22, 2026 — Generic Delta MarketTrendRelation (development; runtime acceptance pending)

**Source implementation:** Adds `Delta` to existing GlobalSetting Trigger, SingleMarketSetting Trigger, and SMS OffTrigger `MarketTrendRelation` values. A Delta compares **the same registered numeric metric** for the selected MarketTrend at the current pinned observation and **one configured `TrendMinutes` earlier**. For completed-period metrics, the prior evaluation is the prior completed source period; for rolling metrics, the two aligned rolling endpoints are separated by exactly `TrendMinutes`. No new user field, separate comparison period, indicator, metric-registry name, exchange endpoint, trading preset, or live settings are introduced. The catalogue remains 45 names.

**Evidence correctness:** Fixed current eligible global/asset cohorts are reused for the historical calculation, with the same MarketTrend definition/currency/reference. Both observations require valid finite numerical values, distinct ordered source timestamps, and complete source history; otherwise Delta is `UNAVAILABLE`. History requirements add one full configured trend for Delta source dependencies; funding Delta requests its preceding verified settlement period. Neither hidden data fallbacks nor synthetic zero values are permitted. The existing three-valued unavailable trigger semantics, 300-second freshness policy and independent runtime settings delivery remain unchanged. Optional Delta is calculated only for metrics explicitly referenced by Delta triggers. Decision history retains both current and previous source timestamps.

**UI and verification:** Monitor editor exposes Delta relations for global triggers, SMS triggers and SMS OffTriggers, with value-based triggers preserving existing bounds. The **sole** existing TestMode analyzer is versioned v2.6.0, placing diagnostic G10 first and adding SMS14 without changing previous diagnostic definitions. Focused source tests cover historical Delta arithmetic, 90% fixed cohort, missing/duplicate source time, opt-in computation and Monitor settings roundtrip.

**ACCEPTANCE NOT YET VERIFIED:** This commit is prepared without a .NET runtime in the editing environment; compiled solution, automated focused harnesses and deployed LONG/SHORT TestMode results remain pending. Do not claim a PASS until owner-run .NET tests and current-source runtime evidence are reviewed.

## November 21, 2026 — PricePathEfficiency and MedianPricePathEfficiency source development

Two **regime-descriptive** metrics are added as one source-candle path family: asset **PricePathEfficiency** and global **MedianPricePathEfficiency**. Registration increases the published names from 43 to 45. The global median is equal-market and uses the frozen eligible cohort with existing 90% coverage semantics. Asset dependencies computed for the median are published for Monitor display without being activated as SMS triggers.

The calculation uses consecutive, fully closed normalized candle closes spanning any configured TrendMinutes and the same pinned rolling endpoint as PriceTrend. Price changes are accumulated as logarithmic steps; the ratio of absolute net movement to total absolute movement is in 0–1. One-step, flat, stale, missing, misaligned or incomplete paths are UNAVAILABLE. Fiat-enabled trends require contiguous same-timestamp quote-to-USDT conversion closes. There are no new Binance endpoints, fixed horizons, retention changes, strategy modifications, or edits to the existing 43 formulas.

Market Analyzer now recognizes the global metric in Path Quality and presents both outputs as nondirectional, dimensionless values. Focused tests cover the new registry scopes, mathematical properties, missing and stale observations, 90% global coverage, Fiat conversion and opt-in publication. The isolated test fixture is versioned v2.4.0 with both metrics enabled on M30. **Build, focused .NET tests and configured runtime TestMode acceptance remain unverified**, because the editing environment did not have the .NET SDK.

## November 19, 2026 — Separate verified newly listed market history from Metric Warnings

**Source implementation (not yet .NET-built or runtime-accepted):** Binance Futures existing `/exchangeInfo` ingest retains source `onboardDate` in cached market metadata and exposes a nullable verified listing timestamp. New `NewMarketHistoryClassification` evaluates already-computed per-asset invalid snapshots strictly for required history **predating that listing**, with available, current, contiguous 5m source candles beginning at the listing boundary. A missing, invalid or unverified onboard date, a later cache start, post-listing gaps, stale/invalid observations, insufficient configured retention or an unrelated failure **never** authorizes suppression.

**Reporting only:** `RaidMetricWarnings` removes qualifying per-asset pre-listing shortages from WARN-level metric groups and instead emits `+ New markets with insufficient history: N` plus one INFO detail per newly affected market or changed set of metric/timeframe results: `+ SOXXUSDT is too new (listed yyyy-MM-dd HH:mm UTC); excluded from N metric results requiring pre-listing history. Other valid metrics remain active.` Unchanged detailed notices are not re-emitted on every raid; the count remains. When a market no longer has such exclusions in the current analysis, report that transition once. Multiple SMS callers are inspected without the display-only five-caller cap; mixed/unverified callers remain warnings.

**Correctness boundaries:** All original calculated metric values, `UNAVAILABLE` flags, eligible cohorts, global coverage denominators, trigger truth values, GlobalSetting/SMS decisions, SOM, source freshness and delivery behavior remain unchanged. Invalid numeric taker fields, missing post-listing candles, stale data, absent listing metadata, unknown metric names and globally indeterminate decisions remain WARN. `NO_TRADES` retains independent DEBUG-only treatment. The classifier is only applied to Binance Futures and reuses its already loaded normalized data/metadata; no new exchange endpoint or full-market re-scan is introduced.

**Focused tests added, execution pending:** `Tests/Phase1MetricWarnings/Program.cs` adds two groups to exercise multi-family shortages, source-backed listing boundaries, missing onboard dates, post-listing gaps, truncated local caches, genuine malformed taker values, multiple SMS callers, persistent-but-nonrepeated INFO messages, recovery notices and retained GlobalSetting fail-closed warnings. Source inspection does **not** establish build PASS; run `dotnet build .\PTMagic.sln` and `dotnet run --project .\Tests\Phase1MetricWarnings`, then confirm real LONG/SHORT test-mode raid output. Do not claim observed warning reduction or correct live listing dates until deployed and checked against fresh logs. Application source, tests and documentation were changed on the existing `phase1/market-data-foundation` branch; no personal strategy/settings files or separate documentation repository were modified by this feature.

## November 19, 2026 — Trading-need-first metric reassessment (documentation-only design authority)

**Status and scope.** This review supersedes earlier *proposed metric-development sequence/quotas* wherever the roadmap treated Phase 4/5 order, an assumed target metric count, a readily available input, or 96 hours as a reason to build or reject a metric. Earlier factual source/test evidence remains valid for the exact revision cited. **No metric is approved, removed or changed by this review; no source/strategy/settings writes are authorized.**

### Required development hierarchy

1. **Trading need:** State which LONG/SHORT regime, entry, exit or risk decision requires recognition of a particular market behavior.
2. **Analytical requirement:** State which observable information reliably distinguishes that behavior from an important alternative.
3. **Existing coverage:** Determine whether present metrics, raw observations, multi-horizon combinations or retained prior values already answer the question.
4. **New measurement:** Only for a demonstrated gap, specify the smallest justified measurement and explain the incremental decision value.
5. **Data requirements:** Derive source resolution, time windows, lookback, historical depth, timestamp, freshness and market coverage from the measurement itself.
6. **Acquisition feasibility:** Validate Binance source/API semantics, both LONG and SHORT instances' shared-IP weight, cold/warm/gap recovery, storage, computation and decision deadlines.
7. **Implementation:** Only after justification and feasibility: adjust ingestion, retention, calculations, dependency selection, historical evidence, triggers, export/UI and focused/runtime verification.

**No fixed 96-hour constraint.** `StoreDataMaxHours=96` belongs to the current diagnostic fixture, not to analytical requirements. Retain more (or less) history when warranted by trading need and mathematically necessary observations; never develop a metric *merely* because the current store already contains its inputs. PTMagic must be able to obtain and maintain required current/recent observations itself; do not assume any external historical database. The existing 300-second pinned-decision-age guard is separate from retention and must not be relaxed to disguise a slow first acquisition.

### Current catalogue — what exists

At functional source [`561bbc32`](https://github.com/HojouFotytu/PTMagic/commit/561bbc32c73a567c65348bf655328c5e7a1f5853), the [registry](Core/MarketAnalyzer/MarketData/MarketTrendMetrics.cs) contains **43 canonical names (30 global-capable / 13 asset-only)**; `PriceTrend` also supports asset scope (14 asset-capable names). These are **not 43 independent information sources**. D-053-removed open interest, top-trader ratios and inferred positioning remain out of scope.

| Existing family | Count | Registered measurements | What they describe |
|---|---:|---|---|
| Price and market structure | 6 | `PriceTrend`, `MedianPriceTrend`, `MeanMedianSpread`, `Breadth`, `Dispersion`, `Concentration` | Return direction, participation, disagreement, volume-ranked return leadership |
| Relative performance and BTC dependence | 5 | `RelativeStrengthMarket`, `RelativeStrengthBTC`, `CorrelationToBTC`, `MeanCorrelationToBTC`, `MedianCorrelationToBTC` | Asset/benchmark strength and BTC synchronization |
| Volatility | 3 | `ATRP`, `MedianATRP`, `VolatilityBreadth` | Within-asset range and its market distribution |
| Volume and turnover leadership | 8 | `VolumeExpansion`, `MedianVolumeExpansion`, `VolumeExpansionBreadth`, `StrongVolumeExpansionBreadth`, `VolumeConcentration`, `TopNVolumeShare`, `BTCVolumeShare`, `BTCETHVolumeShare` | Activity change and distribution of quote turnover |
| Settled funding | 7 | `FundingRate`, `FundingSum`, `MedianFundingRate`, `PositiveFundingBreadth`, `NegativeFundingBreadth`, `ExtremePositiveFundingBreadth`, `ExtremeNegativeFundingBreadth` | Observed Regular funding settlements and their cross-market distribution, **not** verified positions |
| Aggressive taker flow | 6 | `TakerBuyRatio`, `TakerImbalance`, `MedianTakerBuyRatio`, `BuyerDominanceBreadth`, `StrongBuyerDominanceBreadth`, `TakerImbalanceDispersion` | Aggressive quote-volume balance and breadth |
| Price/flow confirmation and divergence | 8 | Four `Buyer/SellerFlowPriceConfirmation/Divergence` asset flags and four corresponding `VolumeShare` global aggregates | Price/taker-flow directional agreement and weighted flow contribution |

**Latest Phase 3 global coverage behavior:** [`4bbe299e`](https://github.com/HojouFotytu/PTMagic/commit/4bbe299e64180ac1aaa9fe2bfccb342def8acad3) lowered the four flow-price `VolumeShare` metrics from 100% to **at least 90% observed coverage of the frozen eligible market cohort**. Verified zero-trade markets count as observed with zero quote volume; missing/invalid markets count against coverage and contribute neither volume nor invented zeros. The denominator contains **observed traded quote turnover**, so 90–99% market-count coverage can still bias the share if missing markets had substantial turnover. Per-asset 0/0 taker ratios stay unavailable; zero combined denominator remains unavailable. This change **does not** relax the 100% completeness required by unrelated volume-concentration metrics. Older 100% text in previous dated sections and in `MarketTrendHistoryRequirements` diagnostic prose is superseded for these four Phase 3 globals, not evidence of source behavior.

### Redundancy and marginal-information audit

| Metric relationship | Actual overlap | Assessment, without automatic removal |
|---|---|---|
| `TakerImbalance = 2*TakerBuyRatio - 1` | Exact algebraic transform of the same source | Retain a convenience alias if it helps triggers; **not an independent confirmation**. |
| `MeanMedianSpread` | Full cross-sectional mean minus median; published `PriceTrend` may use an outlier filter | Derived diagnostic, not a separate market source. Do not substitute filtered `PriceTrend` as the exact full mean. |
| `VolumeExpansionBreadth` / `StrongVolumeExpansionBreadth` and `BuyerDominanceBreadth` / `StrongBuyerDominanceBreadth` | Nested threshold descriptions of one distribution | Keep both only when the stronger versus ordinary condition changes trading decisions. |
| Positive/negative and extreme funding breadths | Overlapping distribution tails; zero-rate assets mean positive and negative are not necessarily complements | Evaluate marginal risk information beyond median/sign; do not infer crowding. |
| `VolumeConcentration` / `TopNVolumeShare` and `BTCVolumeShare` / `BTCETHVolumeShare` | Shared turnover distribution with different concentration/leader definitions | Potentially complementary; test usefulness, not just correlation. |
| Four Phase 3 asset flags | Four price-sign/flow-sign classifications (with neutral/missing states) | One information classification exposed as four binary trigger conveniences. |
| Four Phase 3 global flow-price shares | Different net-aggressive-volume-weighted conditional contributions | Distinct aggregates, **not** four independent predictive signals; compare with basic return/flow/breadth. |
| `ATRP` versus `Dispersion`; market versus BTC-relative strength | Different underlying phenomena despite possible empirical correlation | Keep distinct. ATRP is within-asset price range; dispersion is across-asset return disagreement. |

**Disposition:** Preserve registered interfaces until an evidence-based decision. Prefer shared underlying calculations and explicit derived-label semantics; do not perform arbitrary deletion or proliferation to meet a metric-count objective.

### Trading decisions still not fully supported

| Trading need | Existing inputs | Important unanswered distinction / assessment |
|---|---|---|
| Sustained trend versus chop | End-to-end returns, ATRP, breadth, dispersion, multi-horizon analysis | Same endpoint return may hide very different reversals. **Investigate** directional path efficiency/persistence from contiguous closed candles; compare with existing return+volatility before approval. |
| Regime transition/recovery/deterioration | Current breadth, flow, returns and volatility across horizons | The *history of those values* may distinguish strengthening from weakening without another registered metric. Prioritize provenance-preserving metric histories and comparisons. |
| Fast adverse directional shock | Short-window returns, ATRP, dispersion, breadth and taker flow | Conditional: demonstrate an important move these already fail to identify before adding a shock metric. |
| Entry/exit executable liquidity | Quote turnover and expansion | **Genuine missing source information:** fresh bid/ask spread, opposing order-book depth and expected impact for intended order notional. Evaluate native-source cadence, stale data and per-symbol API cost before any broad acquisition. |
| Carry and reference-price risk | Historical settled funding | Next event timing and mark/index deviation may matter for positions held across funding; conditional on actual decisions, not a license to reconstruct absent leverage/positioning. |
| Portfolio risk of another position | Market correlation and market regime | Integrate real open positions, leverage/margin/capital and correlated exposures; this is not automatically a market-wide metric. |

**Candidate example, NOT approved:** a directional price-path efficiency measurement such as `abs(ln(Cend/Cstart)) / sum(abs(ln(Ci/C(i-1))))` over an explicitly chosen contiguous closed-candle interval, with direction supplied by the existing return and a specified zero-movement validity rule. It distinguishes direct movement from repeated reversals; incremental value against existing volatility/return combinations is **not yet established**. Also investigate whether rolling *fully closed* windows, rather than only completed UTC H1 blocks for Phase 3, materially improve detection timing without treating incomplete candles as valid.

### Data, acquisition and evidence gates

- **History follows calculation:** a daily ATR(14) needs 15 complete daily trend bars (360h) **plus** alignment/retention slots; the existing all-alignment history report gives 384h. Short-window efficiency needs its selected contiguous candle path; change/persistence comparisons require retained time-stamped derived values. No 96h exclusion or automatic expansion.
- **Resolution/freshness:** begin with PTMagic's 5m closed normalized candles and validated native funding. Shorter bars or book sampling require a distinct decision justification. Preserve observation-window and source timestamp separately from pinned raid time and delivery time.
- **Population and completeness:** retain frozen eligible symbols and actual valid subset. Report count and economic/turnover coverage where meaningful; never count missing as zero. Each family keeps its own required completeness; acknowledge subset sensitivity of Phase 3 90% shares.
- **Acquisition:** calculate aggregate Binance IP/provider demand across both bots, warm/cold/partial-repair scheduling, rate-limit backoff, memory/disk, normalized-store correctness and 300s pin age. Older source `24d70402`: first uncached raid ~5m21s and correctly deferred, warm raids ~19–25s; no performance claim for proposed extra history/endpoints.
- **Source-history restriction:** no hypothetical external historical datasets. Newly sampled spread/depth cannot be retrospectively reconstructed from OHLCV. Historical comparisons must avoid look-ahead and population-survivorship artifacts.
- **Evaluation:** test concrete LONG/SHORT regime/entry/exit/risk decisions, compare against current metrics alone, and distinguish correctness, data availability, incremental classification information and measured trading outcomes. TestMode activation and successful calculation are **not** evidence of better trading.
- **Design approval:** no new registry, retention change, additional API endpoint, risk control, analyzer preset or personal DEFAULT settings until a measurement has demonstrated necessity, source reliability, acquisition capacity and integration tradeoffs.

**Immediate analytical order:** audit existing combinations against representative market behaviors; assess persistence from retained metric histories; evaluate path quality and executable liquidity as leading *candidates*; evaluate risk-shock and mark/funding context only if decisions require them; specify source/history requirements and implement only justified measurements. This replaces metric-family completion quotas, not prior factual implementations.

**Verification boundaries:** This document alone is not a user-run test. Prior 43-metric deployment logs from `24d70402` showed H1 719/719 Phase 3 observations, seven raids/bot and six conclusive post-cold/bot, but predate `4bbe299e`; do **not** attribute newer 90%/no-trade live acceptance to those logs. Current `Tests/Phase1COhlcv/Program.cs` registers **22 focused groups** (historical 20/20 PASS predates later additions); newer 22/22 execution is not established here. Source `561bbc32` adds an isolated G08-first TestMode fixture but not proof of fresh decisive G08 selection. No PT writes, trading settings or application-source changes are part of this documentation-only update.

## November 17, 2026 — No-trade normalization and 90% Phase 3 global coverage

Verified zero-trade periods are now identified from complete, aligned Binance 5m candles with source-confirmed zero quote volume, base volume, taker-buy quote volume and trade count. These periods remain UNAVAILABLE for per-asset TakerBuyRatio, TakerImbalance and Phase 3 flow flags, without inventing 0/0 values. Source contradictions, absent fields, incomplete candles and stale data remain real warnings. Verified inactivity is recorded as a grouped DEBUG summary rather than repeated WARN entries.

Phase 3 global *quote-volume share* metrics now use the standard **90% minimum fixed-cohort coverage** instead of the former 100%. Valid traded symbols contribute to the measured numerator/denominator; verified no-trade symbols count as observed with exactly zero volume; other missing/invalid markets do not contribute to either sum and still count against the frozen eligible-population coverage threshold. A zero combined denominator remains UNAVAILABLE. This is a deliberate relaxation: results with 90–99% coverage are based on the observed subset and can differ from a complete-population share. No new endpoint, metric name, PT delivery behavior, SOM behavior or settings fixture changes. Added focused OHLCV and warning regression groups; local .NET and live-run checks are still pending.

## November 17, 2026 — Dev 10 Phase 3 and SMS logs, verified source boundaries

**Current app source [`303b2e7`](https://github.com/HojouFotytu/PTMagic/commit/303b2e7f98d33a5d9cb07c13cf016406a1a49804).** `cbe919aa` adds eight Phase 3 confirmation/divergence metrics using the existing taker-source candle traversal rather than calculating rolling PriceTrend again, with **100% global cohort coverage**; `33a33f0` corrects H72 *test* alignment; `3a25105` updates the existing direction-neutral TestMode fixture: **43 metrics (30 global-capable/13 asset-only)**; 10 trends, 9 GS (`DEFAULT` final), 12 SMS, all eight new metrics on H1.

**User-run proof:** solution build **PASS 16.9s** at `cbe919aa`, `Phase1COhlcv` **20/20 PASS** after `33a33f0`, `Phase2AdaptiveAcquisition` **9/9 PASS** (43-name catalogue). Later solution build **PASS 16.0s** at `fd4aa2e`. `ConfigurationLifecycle` **84/84 PASS** after `70146fa` and again at latest [`303b2e7`](https://github.com/HojouFotytu/PTMagic/commit/303b2e7f98d33a5d9cb07c13cf016406a1a49804). **The new 21st OHLCV reason-category test added at `f45360d` has NOT been run**; do not claim 21/21.

**Deployed TestMode:** LONG/SHORT `PTMagic-Logs-2026-11-17T08-04-32Z.zip` / `08-04-36Z.zip` ran `24d70402` (older than final logs/diagnostics). **7 raids each, 6 conclusive post-cold each, 19–25s steady, first 5m21s deferred, zero PT writes/ERROR/CRITICAL**. H1 Phase 3 shares valid **719/719** and changed on the UTC hour boundary; Phase 3 SMS active **682–692** assets. G08 GlobalSetting not decisively exercised after the cold first raid because earlier GS matched. M15/M30 taker metrics reported 2–4 warning groups for seven M15 and one final M30 market; the old combined reason cannot prove zero-volume source.

**Log cleanup:** `fd4aa2e` outputs one SMS-evaluation console line, dot per 25 markets, one DEBUG file summary and no market/SMS DEBUG. `70146fa` fixes unrelated stale Monitor assertion. `303b2e7` removes redundant compiler dots and adds an active-SMS one-line regression. `f45360d` distinguishes source missing/unverified, invalid numeric and zero-total-volume `0/0`. All calculations/UNAVAILABLE, NLog DEBUG level, PT delivery and SOM behavior unchanged. **84/84 tests PASS**; live post-change log acceptance pending.

**Next:** run `dotnet run --project .\Tests\Phase1COhlcv` (expect 21); optionally confirm G08 first in a temporary isolated TestMode fixture and collect newer logs for warnings/console. Cold-first-raid performance is separate, with the 300s pinned age check intact. Phase 4/5 remain planned; independent LONG/SHORT trading settings untouched. [Full handoff](https://github.com/HojouFotytu/PTM_2.10_Roadmaps_Handoffs/blob/main/handoffs/2026-11-17-ptm-210-dev10-to-dev11.md).




## November 17, 2026 — Remove redundant SMS compilation progress dots

After introducing one progress line for the complete SMS evaluation, the separate `SettingsHandler.CompileSingleMarketProperties` routine still printed one dot per activated SMS and a following newline. That produced standalone `.` lines in the configuration lifecycle output even though the evaluation itself passed the first progress-format test. Remove only that compilation-time console output (and its conditional newline in the exception path). The one evaluation progress line stays in `Core/Main/PTMagic.cs`; SMS property compilation, matched setting records, three-valued safety, error logging, persistence and PT delivery are unchanged. Extend the existing `ConfigurationLifecycle` console-capture regression to include an actively triggered SMS and assert that evaluation plus compilation produces exactly one line.


## November 17, 2026 — Monitor 8 live acceptance, Logs updates and quieter acquisition output

**Source/evidence:** Current Monitor continuation is in the [Monitor 8 handoff](https://github.com/HojouFotytu/PTM_2.10_Roadmaps_Handoffs/blob/main/handoffs/2026-11-17-monitor-8.md), separate from ongoing Phase 3 analytics acceptance. Source version for latest related application logging change `fd4aa2e`. No changes to Analyzer/DEFAULT trading strategies.

- **PT Data schema 1.3 real multi-page PASS:** `pt-history-v3`, one-based pages, 10 records/page; real `All available` GeorgeLong archives `2026-11-15T13-26-59Z` and `13-35-29Z` each have 33 buys/4 pages and 22 sales/3 pages; GeorgeShort archive `2026-11-17T02-11-53Z` has 41 buys/5 pages and 54 sales/6 pages. ZIP CRC valid, dataset status available, `complete_for_selected_sources`, clean end-of-available-history termination, no duplicate history IDs in both histories. **Live bounded UTC 7d/1m/3m/6m cutoff remains pending**, while 26/26 simulated tests and CI build PASS. Scan success is not a lifetime/atomic account-history guarantee. Historical schema-1.2 page-repeat failure is superseded.
- **Logs ZIP:** User's older-format `PTMagic-Logs-2026-11-17T04-07-52Z.zip` passes CRC and includes five full source log files (one PTMagic daily and four Monitor daily logs), independent of viewer filters; no contemporaneous complete server-side directory inventory. `829dea92` names future ZIPs `PTMagic-Logs-<Analyzer InstanceName>-<UTC>.zip` with PT Data safe name rules and unchanged ZIP bytes/authorization; six focused naming cases PASS, live renamed ZIP pending.
- **Raid-summary live UI:** `24d70402` updates the upper previous-raid summary on the complete final RAID SUMMARY marker, rather than waiting for the following raid start. Retains current raid details through next start; partial summaries not promoted. Four focused JavaScript simulations PASS; user .NET solution build PASS 16.5s. Live browser rollover acceptance pending.
- **Log-volume reduction:** `f1d892cd` first-seen-date metadata INFO header and console-only dot/market; `165a2d13`+`b439c00e` Binance Futures normalized 5m batch DEBUG header and console-only dot/attempt; `8706aa87`+`e6df08a7` single SMS property-build INFO start/end and console-only dot per applied SMS; `fd4aa2e` SMS *trigger evaluation* one console progress line/dot per 25 markets and one DEBUG summary, removing repetitive per-market/SMS DEBUG and every-ten-market INFO. Preserve warning/error/critical/RAID TIMING, MarketTrends history requirements and per-trend analytics DEBUG. **File DEBUG logging threshold unchanged.** `b439c00e` solution build user PASS 16.1s; later `e6df08a7`/`829dea92`/`fd4aa2e` builds and live output checks still pending. No measured overall byte reduction yet.

**No source code was changed by this changelog update.**


## November 17, 2026 — Single-line SMS evaluation logging

`ApplySingleMarketSettings()` no longer emits per-market or per-SMS DEBUG events, nor the INFO message every ten markets. Across the complete SMS pass, one incremental console line (`SMS evaluation (N markets): ..... done (M active markets).`) receives one dot per 25 completed markets. At completion one DEBUG file-log record summarizes the entire pass. Default NLog routing sends INFO+ to console and DEBUG+ to file, so it does not duplicate the progress line on normal installations. The per-SMS-name INFO count loop in the raid summary was also removed; the aggregate active-market count and in-memory/JSON SMS results remain unchanged.

All activation/off-trigger evaluation, captured observations, metric-warning grouping, three-valued safeguards, retained SMS summaries, compiled PT properties, stop-processing behavior, TestMode safeguards and actual PT delivery are unchanged. Genuine errors remain logged. Focused `ConfigurationLifecycle` adds a 51-market console-capture test proving one line with three progress dots and no repeated per-market/per-SMS output. Compilation and execution at this revision are pending.


## November 17, 2026 — More specific taker-volume availability diagnostics

The existing taker-flow calculator now distinguishes (1) missing normalized candles, incomplete/misaligned candles and absent/unverified taker fields, (2) an entirely present but **zero-total-quote-volume** period (undefined 0/0 ratio), and (3) invalid nonfinite/negative/out-of-range taker-buy or total quote-volume values, including invalid accumulated totals. The messages are stable per failure class so raid-warning grouping by reason is preserved. A raw taker field rejected by the Binance kline mapper becomes null in the normalized source and is reported as **unverified**; this implementation cannot recover its raw parse-failure category from the existing cache. Neither calculation, validity/UNAVAILABLE behavior, requests, persistence, scopes nor PT delivery are changed. Existing source-candle collection and store validation remain strict.

Focused `Tests/Phase1COhlcv` cases now check missing candle, null taker field, zero valid-period quote volume, a positive total containing a zero-volume candle, and defensive invalid taker volume. Successful application build and configured LONG/SHORT runtime acceptance at this exact revision remain unverified pending the user's next execution.


## November 17 — Phase 3 aligned price / taker-flow conditions (source change)

Eight opt-in metrics: four asset-only directional binary flags and four global net aggressive quote-volume shares. Buyer/seller confirmation or divergence compares positive/negative *open-to-close* price change with the sign of `2*TakerBuyRatio-1`; zeros are neutral, and missing data is UNAVAILABLE. Global value = 100 × sum of absolute net aggressive quote volume for matching symbols / total eligible cohort quote volume. These are dimensionless percentages and require 100% eligible-market source coverage to preserve the denominator. This is neither participation breadth nor a prediction of future price.

Crucial performance contract: the existing `TakerFlowFeatureEngine.CalculateRatio` source candle pass captures the first open, final close and sum of quote volume **only if a Phase 3 metric requests it**. Phase 3 reads these immutable aligned observations without traversing source candles again. The existing rolling `PriceTrend` is not recalculated. Existing global/asset metric selection, per-trend frozen population, cache-v2 repair, source timestamps, validity, three-valued triggers and SOM are preserved. No new Binance endpoint, historic lookback, settings preset or Monitor page is introduced. The existing diagnostic settings fixture remains unchanged.

Focused new fixtures cover four condition directions, quote-volume units, valid zero, missing taker source, global completeness, exclusion of the partially completed next UTC period, and H72 history capacity. New-source build, focused harness execution, and configured TestMode acceptance must be established separately from this source publication.


## November 17, 2026 — Monitor current behavior and acceptance boundary

**Application source anchor:** [`aec54eb`](https://github.com/HojouFotytu/PTMagic/commit/aec54ebfde54b098116fb255c70500da9a458b1f), branch `phase1/market-data-foundation`. Later analytics/Phase 2B commits do not, by themselves, verify these browser features. This section supersedes older contradictory Monitor instructions.

- **First-raid availability:** Monitor's HTTP server starts without `_data/LastRuntimeSummary.json`; no global 60-second startup wait. Shared page setup tolerates a missing/in-progress summary. `FirstRaidDataFilter` gates the dependent Market Analyzer, Status & Summary, Manage SMS, DCA Calculator, and named dashboard/ticker/asset AJAX sections; independent Login, Logs, PT Data and settings routes remain available. Dependent sections say **“Data Unavailable until PTMagic completes its first raid”** and link to **Control → Logs**. Dashboard/ticker checks retry after **60 seconds**. A readable, valid summary with `LastRuntime > Constants.confMinDate` is required, not mere file existence. Replaces superseded global-wait commit `b3d9609` via `385e404`, `35d852e`, `3f94d971`, `3f00b9c`, `74d222e`.
- **Logs:** Password-protected live SSE/FileSystemWatcher viewer; five seconds is missed-event recovery, **not** 5s UI polling. If a previous raid summary exists, display that summary and live current-raid changes as before. **If no completed raid exists**, replay the PTMagic startup log from byte zero and show pre-raid lines and the first raid, rather than withholding entries until a new raid marker; subsequent raids preserve the previous completed summary. Monitor log preview retains bounded recent lines and separate historical warning/error panel. Source: `972c343`, `2fd4806`. **Download Logs ZIP** is a separate, authenticated `Logs?handler=Download` handler: includes all top-level disk files from PTMagic and Monitor `_logs`, organized `PTMagic/*` and `Monitor/*`, irrespective of browser history/search/severity/visible lines/date. Streamed disk-backed archive; ignores reparse-point links, tolerates concurrently written logs, and is **not an atomic snapshot**; rotation may remove a file mid-export. Source: `ad887bf`, `fb09dad`, fix `82ccf4a`. Authenticated ZIP contents/large-file live acceptance not established.
- **Asset table:** Market-wide values come from `MarketTrendAnalytics.json`, trading rows and legacy trends from `LastRuntimeSummary.json`. No Monitor-side Futures re-filter (`74da6c1`). Absolute/Relative market rows unite summary symbols and symbols with nonempty published `AssetMetrics`; expanded detail displays **all already-published metric keys** (including PriceTrend, global-dependency calculations, unregistered keys, trends with display flag off), regardless of acquisition reason, trigger selection, registered asset scope or `AssetSymbols` membership. Keep native value under its originating MarketTrend column; do not create a new table/page or acquire/recalculate anything just for display. Unpublished internally acquired data cannot appear. Source `e7a74ea`, `99cbf01`, `ef8f2cb`. Previous rule “only explicitly configured asset-scoped metrics in displayed/selected trends; omit PriceTrend” is **superseded**.
- **Version/build:** Tooltip over entire visible version (`v2.10.0`) obtains build ID from `Core.Helper.BuildIdentity.CommitBuildNumber` (`MMddyyyyHHmm` KST from stamped Git commit time), **not** the last field of `PTMagic.dll` `ProductVersion`; version prefers running-bot summary with executable fallback; unknown metadata says “Build unavailable.” Source `65e82e6`, `aec54eb`. Match deployed PTMagic/Core builds: Monitor's local Core timestamp alone does not prove the PTMagic binary version. Live hover acceptance not yet confirmed.
- **PT Data (separate exporter):** Active source/manifest `pt-history-v3` / **schema 1.3**, not 1.1/1.2: PT 2.6.1 paging is **one-based** (`page=1` onward, `perPage=10`; underlying offset `perPage*(page-1)`), actual oversize reduction **10→5→1**. Bounded `7d/1m/3m/6m` stops after descending first page reaches UTC date cutoff; `all` traverses all available history. Raw JSON and original fields retained. See [contract](https://github.com/HojouFotytu/PTMagic/blob/phase1/market-data-foundation/Tests/PTDataExport/CONTRACT.md). The old real **schema 1.2** repeated-page failure is historical, not evidence that 1.3 failed or succeeded. **26/26 simulated HTTP→ZIP tests** and Monitor build PASS in [run 43695709351](https://github.com/HojouFotytu/PTMagic/actions/runs/43695709351); fresh real multi-page acceptance **unverified**. `GET /api/v2/data/misc` option still deferred.
- **Validation:** [CI 43915093122](https://github.com/HojouFotytu/PTMagic/actions/runs/43915093122): 26/26 HTTP fixture groups PASS, .NET 10 Monitor Release build PASS, Linux web-listener smoke test without a first-raid summary PASS. [CI 43908846493](https://github.com/HojouFotytu/PTMagic/actions/runs/43908846493): Logs ZIP compile fix/build/startup PASS. These do **not** validate authenticated browser streaming, ZIP integrity, tooltip hover, complete first-raid transitions or real PT API paging. Owner subsequently reported Monitor accessible; do not infer other unobserved UI acceptance. No changes to Analyzer acquisition or personal LONG/SHORT trading strategies.


## November 17 — Phase 2B configured TestMode runtime acceptance (two instances; cold-start still pending)

**Evidence:** User-supplied `PTMagic-Logs-2026-11-16T16-59-19Z.zip` (GeorgeLong) and `PTMagic-Logs-2026-11-16T16-59-24Z.zip` (GeorgeShort), each containing `PTMagic/2026-11-17.txt`. Both initially ran exact compiled source `ef1f738a1e945db34f4b403f491c0cd4340835e6` (four Core fixes at `0b99efc`, regression fixtures/changelog `ef1f738`); the final restarted raid shows `82ccf4ad7f457e3da9b0fa53067c8eb31a85df90`, a later descendant. **18 completed raids total (nine per instance), 16 post-cold-start raids produced conclusive TestMode configuration evaluations, zero post-warmup metric warnings, no PT writes and no PTMagic ERROR/CRITICAL log entries.** Both initially deferred for age above the unchanged 300s pin-based freshness gate; first raid duration 5m24s and cold normalized 5m fetch ~302.7s/instance for 829,726 candles/719 markets. Cold-market metadata was ~15.1–15.4s; legacy 1m reconstruction was explicitly skipped. **Cold start is not accepted under the 300-second decision deadline.**

**Recurring latency correction — runtime observed:** None of the 16 steady-state/restart raids repeated the earlier ~182s, >500,000-candle H48 refetch every 15 minutes. Normal normalized 5m refresh was ~7–10s (usually 2,157 requested candles across 719 markets); two warm restarts restored 829,007 validated candles per instance, then requested only 4,294 / 4,305 candles and completed in 26s. The remaining steady-state full raid duration was 19–27s. Configured MarketTrend calculations took ~3.9–5.9s. 10 MarketTrends were evaluated, and the settled funding provider verified 719/719 eligible histories in post-cold raids. All post-cold raid summaries report `Metric warnings: none`.

**Initial selection correction — runtime observed:** Previously, unrelated missing taker observations in property-free diagnostic SMS marked the entire initial selection indeterminate. In the current logs, GlobalSetting `DIAG_G05_VOLUME_VOLATILITY` and 710–713 active diagnostic SMS coexist with conclusive `TestMode preview`/`TestMode unchanged` status; eight evaluated post-cold decisions per instance are represented by 16 total post-cold raids. This supports the new distinction between monitoring-only SMS and property-bearing/stop-process SMS uncertainty; it does **not** prove real PT delivery, permanent SOM-off, browser rendering or safety of missing property-changing SMS under all inputs.

**Scope and remaining acceptance:** Metric catalogue **35 canonical metrics: 26 globally evaluable and nine asset-only**; the diagnostic fixture retains 10 MarketTrends, 8 GlobalSettings (final `DEFAULT` matching the case-sensitive preset directory), and 11 SMS. Source includes exact funding/quote-volume taker validation, selected-only dependency calculation and display of already computed values. Current source-specific user-run `dotnet build` and focused `NormalizedWarmStartCache`/`ConfigurationLifecycle` results were not supplied after `ef1f738`; running binaries are evidence of successful deployment but not those separate harness PASS results. Browser acceptance remains pending. If eliminating **first-ever cold-start** delay is required, design a separate bounded/warmup acquisition fix; do not raise `MaxAnalysisAgeSeconds=300` or fabricate later observation timestamps. The PT Data history-export repeated-page blocker is a separate Monitor issue and remains open.

---


## November 15 — PT Data exporter replacement and live pagination failure (current)

The read-only PT Data ZIP exporter was replaced in [`3c7eddd`](https://github.com/HojouFotytu/PTMagic/commit/3c7eddd9d4ce8694140c6210344253afef8eb41e) with `paged-json-v2` / manifest schema **1.2**. It downloads 10 records per page, adaptively reduces the page size for genuinely oversized responses, preserves raw successful page JSON and original trade details including nested transaction-time diagnostics, and retains partial histories with specific error codes. The [GitHub Actions run](https://github.com/HojouFotytu/PTMagic/actions/runs/43665987850) **passed 20/20 simulated HTTP→JSON→ZIP tests and a .NET 10 Monitor Release build with 0 errors/0 warnings**. Those offline tests do **not** establish that real PT advances pages.

**Live acceptance still FAILS:** User GeorgeLong ZIP `PT-Data-GeorgeLong-2026-11-15T03-00-33Z(2).zip` confirms the new schema/version, but the two raw responses at `page=0` and `page=1` are **byte-identical** for both buys (180,843 bytes/page) and sales (120,514 bytes/page). Each combined history holds only 10 records and reports `history_page_repeated` / `partial`; `range_coverage_complete=false`. No page-size reductions occurred. Position snapshots worked (29 PAIRS, 1 DCA, empty pending/watchmode). The exact cause of non-advancement is **unproven**: inspect the actual outgoing `page`/`perPage` parameters and direct read-only PT results, compare documented pagination semantics, then correct and verify against live PT. **Do not call this resolved merely because offline tests pass.** Account Information `/api/v2/data/misc` remains deferred.

[Current PT Data investigation handoff](https://github.com/HojouFotytu/PTM_2.10_Roadmaps_Handoffs/blob/main/handoffs/2026-11-15-pt-data-pagination-live-evidence-handoff.md). Earlier descriptions of schema-1.1 `perPage=25` and `invalid_or_oversized_data` are historical.


**Updated:** November 17, 2026

**Application version:** 2.10.0, in development

**Application branch:** `phase1/market-data-foundation`

**Latest analytics implementation checkpoint — November 15:** Source revision [`c6b4f9e`](https://github.com/HojouFotytu/PTMagic/commit/c6b4f9e02ec336956a5e763fbe7c2762df6b0bae) completes the planned Phase 2B funding and taker-flow aggregate families at source level: 35 registered metrics, 26 global-capable and nine asset-only. Five new global funding aggregates and four global taker-flow aggregates were implemented across `721e9f9`/`dce43f0`, `718d65e`, `d387354` and `c6b4f9e`. `FundingRate`, `FundingSum`, `TakerBuyRatio`, and `TakerImbalance` remain asset-only. Snapshot display reflects **already calculated** global and per-asset values; publishing an input does not enable it as a trigger or cause additional calculations solely for display. The **newer 35-metric revision has not been built or tested in .NET in this conversation**, nor confirmed in configured TestMode, cache-v2 migration or browser acceptance. Earlier D-053 and 26-metric source-specific passes remain historical proof for their exact SHAs, not acceptance of this revision. Next analytics stage is Phase 3 transparent price/volume/taker-flow confirmation and divergence after appropriate targeted verification. Personal strategy files, analyzers and presets remain independent.

## Monitor — HISTORICAL schema-1.1 PT Data date-range pagination (November 13–14; superseded)

The **Control → PT Data** read-only ZIP exporter now uses **7-day, 1-month, 3-month, 6-month or All available** UTC trade-history windows instead of the superseded 250/1,000/5,000 record limits. Buy and sale history are retrieved from the configured ProfitTrailer target in descending `BOUGHTDATE` / `SOLDDATE` order, requesting `page=0` onward at `perPage=25`. The exporter parses nested source `{hashcode,data}` responses, deduplicates IDs, filters transaction dates, and produces `{data,sourcePageHashcodes}` aggregate history files. Original trade entries and historical `BUYNOTE`/`SELLNOTE` diagnostic `extraInfo -- CV:` contents are retained. The ZIP manifest is schema **1.1**, with date range, source page/record counts, cutoff/end and partial/failure metadata. Current positions (watchmode, pending, pairs, dca), optional funding and password-protected configured-target read-only restrictions are unchanged.

**Implementation:** PTMagic commit [`618b628`](https://github.com/HojouFotytu/PTMagic/commit/618b6285d18e3c655139ae675bce389b72034ee5); later HEAD [`bda2394`](https://github.com/HojouFotytu/PTMagic/commit/bda2394eccd0e0439a8578bf7c7b1c4287e15382) does not change exporter files. The user's **schema-1.0 initial ZIP** verified first-request download, raw nested `data` (23 buys, 25 sales), positions (8 PAIRS, 2 DCA) and transaction-time CV presence, **not** current paginated date-range behavior. Live >25-record pagination, full-window boundaries, authentication and failure handling remain to be verified. The current `range_coverage_complete` flag denotes absence of detected duplicates, **not** guaranteed lifetime history or proof of no page shifts. [Detailed handoff](https://github.com/HojouFotytu/PTM_2.10_Roadmaps_Handoffs/blob/main/handoffs/2026-11-13-monitor-performance-export-design.md).


This is the cumulative record of **2.10 changes that remain in effect**. It covers the normalized market-analysis foundation begun on November 2, 2026, through the current implementation. Entries describe the resulting behavior and are consolidated when that behavior changes. Planned features, superseded implementations, reversed changes and personal trading-strategy work are outside this record.

The same document is maintained in the [application repository][app-copy] for code merges and pull requests, and in the [2.10 roadmap/handoff repository][docs-copy] for project continuity. The [current roadmap][roadmap] tracks future work; [status and evidence][status] track acceptance. D-053 source/audit references are dated historical evidence; its subsequent configured TestMode acceptance is recorded separately. Phase 2B's retained funding/taker aggregates are source-implemented at `c6b4f9e` with **35 metrics (26 global-capable, nine asset-only)**; the current 35-metric source has not been compiled or runtime-accepted. See the updated Dev 9 application/analytics handoff in the roadmap repository. The accompanying template and Monitor help updates make the existing startup SOM default explicit in both supplied general-settings files and explain its TestMode behavior beside the toggle.

**Follow-up correction (November 13):** A configured raid displayed `Build unknown`, revealing that the initial metadata-stamping test incorrectly accepted missing metadata. The source now obtains the commit Unix timestamp through `git rev-list -1 --timestamp HEAD`, avoiding the percent-based format token in the Windows build shell. It extracts the timestamp, **fails compilation when unavailable**, and the existing decision-history test requires a valid 12-digit KST identifier derived from embedded metadata. The user built the corrected source at `146f57336eee28fa5bc5f30116420e323413bf89`: Core, PTMagic and Monitor **PASS (13.1s)**; `Tests/DecisionHistory` **14/14 PASS**; the user subsequently **confirmed the deployed raid display**. Preserve matched `Core.dll` and PTMagic executable deployment.

## November 15 — Fix cyclic taker refetch, unnecessary legacy 1m history and diagnostic SMS blocking (source; acceptance pending)

Source review of 19 raids at `b3d9609` identified recurring approximately 182-second normalized 5m historical fetches every 15 minutes (over 500,000 candles versus 4,459 on normal raids). A new M15 candle previously made the taker repair scanner flag an H48 history deficit before ordinary incremental data had been fetched. `MarketDataService` now measures only missing/null fields **within already cached closed candles**, fetching from the oldest actual deficit with bounded overlap. Source-validity checks and legacy v1 taker repair remain intact.

Cold Futures startup also performed a 1,531.734-second 96-hour legacy 1m reconstruction for 719 markets, despite the diagnostic's normalized Exchange-only MarketTrends. When all configured Futures trends are normalized Exchange trends, the legacy 1m rebuild is bypassed while current ticker data and full normalized 5m history are retained. Legacy/non-Exchange trends preserve the previous historical behavior; Spot and Binance US unchanged.

The 19 completed raids all deferred configuration as selection-indeterminate despite a conclusive GlobalSetting. Unknown **property-free, non-stopping SMS** no longer block a first configuration delivery; missing data remain unavailable and unselected. Unknown SMS carrying PT property modifications or stop-process control still defer the uncertain initial delivery. Global uncertainty, 300s freshness safeguard, startup SOM and TestMode remain unchanged. Targeted regression cases were added in `Tests/NormalizedWarmStartCache` and `Tests/ConfigurationLifecycle`. **Execution of updated tests and configured runtime acceptance remain pending.**

## November 15 — Futures normalized acquisition latency mitigation (source; acceptance pending)

The comprehensive diagnostic 35-metric raid with 719 markets successfully calculated its global/per-asset metrics and saved its Monitor snapshot, but its pinned observation age at decision time was **301.124 seconds** with `MaxAnalysisAgeSeconds=300`. The supplied end-of-raid excerpt does not establish which earlier stage dominated wall time. Source inspection revealed that Futures five-minute market acquisition used only two concurrent refresh tasks and fetched a maximum of 500 candles per HTTP page. The existing shared Futures quota tracker applies **the same weight of five** to 500- and 1000-candle kline requests. The Futures 5m path now permits **four parallel market refreshes** and **1000-candle pages**, reducing a cold 96-hour history from three to two requests per market while preserving candle boundaries and strict validation. Binance Spot, Binance US and legacy 1m kline paging retain two concurrent tasks/500-candle pages.

New `RAID TIMING` INFO messages expose bootstrap/version, Futures market metadata, normalized candle refresh (full/incremental market counts), market-universe preparation, funding history, MarketTrend metrics, Monitor snapshot, GlobalSetting/SMS and delivery durations. The shared atomic 80% Futures IP request-weight cap, response-header/429/418 handling, cancellation, UTC observation pin, funding deadline, cache behavior, `MaxAnalysisAgeSeconds=300`, TestMode and PT delivery safeguards remain **unchanged**. Focused offline pagination and request-weight checks were authored in `Tests/Phase2AdaptiveAcquisition`. New-source build, executed tests and post-change stage-duration validation are pending.

## November 15 — Phase 2B global taker-flow aggregates (implementation; acceptance pending)

Added `MedianTakerBuyRatio` (equal-market median of source-verified per-asset ratios), `BuyerDominanceBreadth` (valid-market percentage with ratio >0.5), `StrongBuyerDominanceBreadth` (valid-market percentage with ratio >= configurable `StrongBuyerDominanceThreshold`, default 0.60, valid >0.5..1) and `TakerImbalanceDispersion` (population standard deviation of already calculated TakerImbalance per-asset observations; a distribution/spread measurement, **not directional confirmation**). Market-wide aggregates weight assets equally; per-asset taker ratios remain quote-volume weighted over the exact latest closed UTC MarketTrend duration. At least 90% of the selected global population must have valid source. New metrics reuse existing normalized kline taker-buy/total quote-volume source, cache-v2 repair, the existing global population/MaxMarkets rules and the Monitor's dynamically published values. Global-only selection calculates only required per-asset ratio/imbalance on the selected global cohort; no extra work is done for display, no new exchange endpoints or new persistent caches. Asset-only TakerImbalance reuses/displays the ratio it already computes. The existing Monitor adds a Taker Flow group with neutral dispersion formatting and four-decimal dimensionless values. Strong threshold is configurable per MarketTrend. Trading trigger selection remains explicit, not inferred from display. The catalogue now has **35 metrics: 26 global-capable and nine asset-only**. Focused tests for formulas, >=90% coverage, missing and invalid source, default/custom thresholds, source reuse and scope are present. Build and configured runtime/browser acceptance remain pending.

## November 15 — Phase 2B extreme funding breadth (implementation; acceptance pending)

Added two opt-in global metrics: `ExtremePositiveFundingBreadth` and `ExtremeNegativeFundingBreadth`. Each measures the percentage of **valid latest Regular funding rates** meeting the corresponding signed threshold (inclusive >= / <=), not a count of historical events or a ratio of the entire nominal universe. MarketTrend properties `ExtremePositiveFundingThreshold=+0.05` and `ExtremeNegativeFundingThreshold=-0.05` supply editable defaults in **funding-rate percentage points** (+0.05 means +0.05%); nonfinite or wrong-sign settings make the affected metric unavailable. The calculation reuses existing per-asset `FundingRate` inputs, native settled-funding history, the frozen global population, and 90% minimum valid coverage. No new source endpoints, per-asset calculations for display, preset edits, strategy edits, or extra acquisition. The existing Monitor Funding group displays both metrics, and the MarketTrend editor exposes their thresholds. Metric catalogue: **31 total** (22 global-capable, nine asset-only). Focused offline checks added for defaults, inclusive boundaries, custom thresholds, incomplete coverage, missing settlements, wrong signs, and nonfinite values. Build/test execution and configured runtime acceptance are separate pending steps.

## November 14 — Phase 2B market-wide funding aggregates (implementation; acceptance pending)

Three new opt-in Binance Futures global metrics: `MedianFundingRate` (median latest verified Regular settlement in the exact configured window, percentage points), `PositiveFundingBreadth` and `NegativeFundingBreadth` (percent of valid latest Regular rates strictly above/below zero; zero is neutral). These use the frozen global population, 90% minimum coverage, native funding history, and existing bulk funding acquisition/cache; no extra exchange endpoint. The calculation/visibility contract now publishes only **already computed** values: funding aggregates require per-asset FundingRate for their exact global population, while FundingSum is computed only if separately selected for asset use. Other already-calculated per-asset inputs (ATRP, volume expansion, BTC correlation and normalized returns) are published under their originating trend; calculated market means/medians are likewise published even when internal dependencies. The Monitor dynamically displays published market-wide and per-asset metrics regardless of trigger selection, retaining existing grouping and the six-decimal signed funding median format. Missing values remain unavailable; trigger eligibility and the metric-selection contract are unchanged. Registry: 29 metrics (20 global-capable and 9 asset-only). Extreme-funding and taker aggregates are still planned; no changes to the diagnostic analyzer, presets, trading settings, or Linux folders. Build/focused tests and configured runtime/browser acceptance for this source remain pending.

## November 14 — Dev 8 to Dev 9 consolidated checkpoint

The commit-stamped build display was **verified by the user in a deployed raid**, following solution build PASS 13.1s and DecisionHistory 14/14 at `146f57336eee28fa5bc5f30116420e323413bf89`. The known `Build unknown` defect is closed. The timestamp is Git HEAD committer time formatted `MMddyyyyHHmm` in KST; semantic version remains `2.10.0`, and the separate full source SHA remains authoritative.

Taker Flow: two asset-only kline-derived metrics are implemented; 26 total (17 global-capable, nine asset-only). `f7d802f` passed build 16.1s and focused test groups 15+10+8 = **33/33**. Configured TestMode coverage, native values, nullable taker history restoration/upgrade to v2, and steady-state incremental requests have **not** been demonstrated. The last supplied November 13 17:00–17:50 log/data archive ran **`e31635a1`**, with old 24-metric analyzer and **519 v1 OHLCV cache files**, and therefore **does not verify taker flow**.

Separate Monitor/PT Data date-window pagination and Linux startup-path code landed in later commits `618b6285` and `bda2394e`; see dedicated Monitor handoff and source-stamped evidence for their validation, without conflating them with the D-053/Taker Flow acceptance chain. The November 14 next action was a configured Binance Futures TestMode raid of the then-current 26-metric analyzer (historical; superseded by the Phase 2B 35-metric source checkpoint). The current verification action is a source-stamped build, focused funding/taker tests and appropriately configured TestMode raids; keep all personal ProfitTrailer LONG/SHORT strategy settings and repositories out of scope.

## Build identity — commit timestamp in raid summaries (November 13)

The semantic application/assembly version remains `2.10.0` in `PTMagic/Program.cs`. The build now embeds the **latest HEAD Git committer timestamp** through the existing Core MSBuild source-revision target and formats it in **Korea Standard Time (UTC+09:00)** as `MMddyyyyHHmm`. Each new commit followed by compilation yields a date/time-based identifier; a restart or later raid of the same compiled executable does **not** alter it. This is a Git-commit timestamp, **not** the current raid time or build execution time.

The console RAID SUMMARY displays, for example, `+ PT Magic Version: 2.10.0 (Build 111320261850)`. The already persisted `Summary.Version` and version/release comparisons remain unchanged; source SHA continues to appear separately. The corrected build **fails** when the Git commit timestamp cannot be embedded; the initial `Build unknown` behavior has been superseded. Multiple commits in the same minute can produce the same 12-digit value; the independent source SHA remains definitive. The existing `Tests/DecisionHistory` harness now checks UTC-to-KST formatting and the required 12-digit representation. The corrected feature is **build-tested and user-confirmed in a deployed raid** at `146f5733` (14/14 focused tests). The identifier is the HEAD commit's committer time in KST, stable across raids/restarts, and changes with new source commits followed by recompilation.

## Phase 2 — Kline-derived taker flow (November 12; offline verified November 13, runtime pending)

**Focused verification — November 13, 2026 (KST):** User-run at PTMagic source [`f7d802facfabb4611098d695d88a269d60daa278`](https://github.com/HojouFotytu/PTMagic/commit/f7d802facfabb4611098d695d88a269d60daa278). `dotnet build .\PTMagic.sln` **PASS in 16.1 seconds** (Core, PTMagic, Monitor net10.0). `Phase1COhlcv` **15/15**, `NormalizedWarmStartCache` **10/10**, `Phase2AdaptiveAcquisition` **8/8** — **33/33 focused test groups PASS**. This establishes source-specific compilation and offline checks, **not** configured live Binance/TestMode metric values, PT settings delivery, or Monitor browser acceptance.


Two opt-in asset-only metrics, `TakerBuyRatio` and `TakerImbalance`, are registered alongside the 24 retained D-053 metrics: **26 canonical metrics total, 17 global-capable and nine asset-only**. They measure the ratio of summed source-verified taker-buy **quote** volume to summed total quote volume over the latest exact complete UTC-aligned `MarketTrend.TrendMinutes` period, and twice that ratio minus one. Both are **dimensionless**; a missing/invalid taker field, incomplete/stale candles or zero total volume is **UNAVAILABLE**, not zero.

`BinanceKlineClient` now parses the existing Binance kline index 10 (taker-buy quote volume), `MarketCandle` preserves it as nullable data, and `TakerFlowFeatureEngine` reads the existing normalized `MarketDataStore`, without another Binance endpoint. Existing population, pinning, trigger, export, funding, and Monitor conventions are retained. The Monitor displays ratio values with four decimals and colors `TakerBuyRatio` around 0.5 and `TakerImbalance` around zero.

The normalized warm-start cache version 2 persists these fields while continuing to **read legacy version-1 OHLCV**. Configured taker trends request only the missing latest complete-period 5m window; existing OHLCV is not discarded. Verified repaired taker coverage forces an immediate cache save, bypassing the existing periodic save throttle. Metric-unaware configurations continue the original refresh pattern. Focused tests and the **existing** diagnostic analyzer have been updated in place.

**Verification boundary:** The Taker Flow implementation compiled and passed 33/33 focused checks at `f7d802f`. Configured **new-26-metric TestMode** acquisition, actual `TakerBuyRatio`/`TakerImbalance` runtime values, v1→v2 cache migration and repeat-raid behavior remain unverified. The November 13 runtime log/archive examined previously ran older `e31635a1` (24 metrics, v1 OHLCV), not the new Taker Flow build. Preceding D-053 PASS at `e31635a1` is historical; do not attach those results to this implementation. No personal LONG/SHORT strategy or settings files were changed.

## Dashboard — restored pre-experiment source and unresolved refresh investigation (November 11)

The entire November 10–11 Possible Buys zero-limit count/card experiment, separate PBL-count endpoint, async-cache variation, and replacement AJAX refresh implementation have been withdrawn by owner direction. The active `Monitor/Pages/Index.cshtml`, `Monitor/Pages/_get/DashboardTop.cshtml`, and `Core/DataObjects/ProfitTrailerData.cs` files are byte-identical to the pre-experiment `438acbe8e40241cd190e45385dffbee5637b3eb8` file blobs. **Current zero-limit behavior hides the Possible Buys card** and skips fresh PBL reads in that mode. The original `.load()` refresh and profit-color initialization are retained.

**Historical pre-fix defect (resolved November 11):** The owner had reported stale PBL and Positions data after the rollback. The later guarded tooltip-cleanup correction restored DashboardTop refresh, as confirmed by the owner; the following investigation narrative describes an obsolete checkpoint. November 2 changes `ec19a5e` and `26ce2d8` made DashboardTop polling callback-dependent, added a hidden-tab guard, and placed tooltip and color updates before the request-running flag is cleared. These are possible source-level failure paths, **not a runtime-confirmed cause**. Ticker tooltip preservation resides separately in `_Layout.cshtml`/`TickerWidgets.cshtml`. The next proposed correction must be described to the owner **before any implementation**, must preserve the existing `$("#dashboardTop").load(...)` method unless explicitly approved otherwise, and must not reintroduce the experimental count endpoint/cache.

Any next refresh correction is restricted to **scheduling logic in `Monitor/Pages/Index.cshtml`**. Preserve the original `.load()` request and callback; do not change PBL/Positions data retrieval, calculations, DashboardTop rendering, ticker refresh, tooltips or autocoloring. The exact proposed change must be explained before source edits.

The last confirmed local focused Monitor run reported **75/75 PASS** on a prior revision, and the last complete suite reported **337/337 PASS** at `fb94373`. Neither establishes correct refresh after the rollback. See the [November 11 investigation and current status](https://github.com/HojouFotytu/PTM_2.10_Roadmaps_Handoffs/blob/main/updates/2026-11-11-dashboard-refresh-history-and-current-state.md).

## Phase 2 — Retained settled funding and D-053 metric removal (November 12)

**Post-removal read-only audit:** [D-053 source comparison and dependency audit](https://github.com/HojouFotytu/PTM_2.10_Roadmaps_Handoffs/blob/main/updates/2026-11-12-d053-removal-code-audit.md) compared the baseline 22-metric pre-introduction source `66629347`, the 27-metric pre-removal source `d101b5a0`, and the retained 24-metric source `892a5f1f`. All original metric identities and scopes were preserved. The removal initially broke a shared funding call to `FuturesOpenInterest.NormalizeSymbol`; commits `02820822` and `d7847c46` repaired all examined funding callers using `FuturesFunding.NormalizeSymbol`. No additional major or critical removal-specific defect identified by source inspection. At the historical audit checkpoint, the current-source .NET build, full 15-harness run and configured raids were not yet independently verified. Subsequent D-053 build, 36 focused checks and four TestMode raids passed at `e31635a1`; do not extend these results to PT writes/browser acceptance.


D-053 initially left **24 canonical metrics**: 17 global-capable measurements and seven asset-only measurements. **Phase 2 Taker Flow subsequently extended this to 26 metrics (17 global-capable, nine asset-only)**; the D-053-only 24-metric registry is a historical checkpoint. The three previously implemented OI/long-short positioning metrics and their source clients, history services, persistent-cache dependencies, acquisition jobs, and UI presentation paths were removed under D-053. Older feature versions and their passing tests remain in Git history and dated development records, not the active metric registry.

**Funding is retained:** `FundingRate` and `FundingSum` are separate asset metrics calculated from verified Regular settled funding events. The application continues to persist `_data/DerivativesFundingCache`, acquire funding in bulk with conditional per-symbol fallback, respect provider quota and response-header/cooldown safeguards, and keep missing histories UNAVAILABLE.

**Source:** current `phase1/market-data-foundation` branch, cleanup published November 12. **Validation boundary:** last user-run .NET solution build/18-harness suite passed at pre-removal source `d101b5a0`. Later user-local build and configured TestMode funding acceptance of D-053 are recorded at `e31635a1`; non-TestMode PT API and browser acceptance are separate. The retired OI/ratio user cache directories are not read by the active code and are not automatically deleted.

## Monitor — enforce centered market-wide headings and gray labels (November 10)

The grouped Market-Wide Metrics page now includes scoped page-local styling to ensure that all column headings, stacked labels, and values render **centered**, while metric labels are **muted gray** and the seven group titles are **white uppercase**. The group titles are emitted uppercase by Razor, not dependent solely on a stylesheet transform. The page-local CSS prevents stale cached `custom.css` from overriding the intended display. Per-Market Metrics, metric values and calculations are unchanged. The focused source check was extended. **Local .NET build and browser confirmation of this latest revision are pending.**

## Monitor — centered two-row Market-Wide Metrics headers (November 10)

The existing seven-group Market-Wide Metrics table now has **two header rows**: white uppercase group titles (PRICE DIRECTION, MARKET STRUCTURE, etc.) on the upper row, with the individual metric labels in muted gray on the lower row. The Trend, Timeframe and Markets headings span both header rows. **Every column is centered**, including the three identity/count columns, group titles, metric labels, and stacked metric values. Grouped metric values retain their existing positive/negative colors, tooltip details, units and UNAVAILABLE behavior. The separate Per-Market Metrics table is unchanged. A focused `ConfigurationLifecycle` source assertion checks the two header rows, centered alignment, and styling.

**Status:** committed on existing `phase1/market-data-foundation`; no local .NET build or browser verification has been reported for this latest change. Prior 73/73 checks belong to earlier source and are not counted as acceptance here.

## Monitor — compact Global Setting header tooltip (November 10)

The `(i)` tooltip beside the selected Global Setting now contains the unlabeled instance name and full selected GlobalSetting name, followed by a single delivery result: **Delivered to PT** only when the current selected setting has a matching confirmed applied setting and the latest delivery is `Applied` or `Unchanged`; otherwise **Not Delivered to PT**. A fourth `Startup SOM` line appears only if PTMagic's recorded startup global-SOM enable was acknowledged. As before, that recorded flag is **not live ProfitTrailer SOM readback**. The status/summary page retains detailed technical delivery and SOM diagnostics. The compact tooltip uses no field-label prefixes; a scoped CSS class prevents long setting names from wrapping.

**Scope:** Monitor-only presentation. PT API/SOM commands, delivery acknowledgement semantics, detailed status reporting and trading logic are unchanged. One focused ConfigurationLifecycle source-regression check was added. **Local .NET build, focused test execution and browser rendering have not yet been verified** on this update.

## Market Metrics — seven grouped market-wide columns (November 10)

The read-only **Market-Wide Metrics** table now groups its original 17 metric values into seven comparative columns: **Price Direction** (PriceTrend, MedianPriceTrend, MeanMedianSpread), **Market Structure** (Breadth, Dispersion, Concentration), **Volatility** (MedianATRP, VolatilityBreadth), **Volume Expansion** (MedianVolumeExpansion, VolumeExpansionBreadth, StrongVolumeExpansionBreadth), **Volume Concentration** (VolumeConcentration, TopNVolumeShare), **BTC / ETH Volume Share** (BTCVolumeShare, BTCETHVolumeShare), and **BTC Correlation** (MeanCorrelationToBTC, MedianCorrelationToBTC). Each column shows a bold group name, stacked metric labels, and correspondingly aligned values per configured MarketTrend.

**No analytics semantics changed:** the existing Trend, Timeframe and Markets columns, metric value formatting/colors, tooltip definitions, unavailability behavior, and the separate Per-Market Metrics table remain intact. CSS is limited to the grouped market-wide table. The grouping inventory was source-inspected: seven groups, all 17 metrics present once, headings and values driven by the same ordered group definitions.

**Implementation:** existing PTMagic `phase1/market-data-foundation` branch, `Monitor/Pages/MarketMetrics.cshtml.cs`, `Monitor/Pages/MarketMetrics.cshtml`, and `Monitor/wwwroot/assets/css/custom.css`. **Status:** committed; local .NET solution build and actual Monitor browser rendering not yet verified on this new revision. Previous 71/71 lifecycle results belong to the earlier change.

## Global Settings Log — positive Market Trends are green (November 10)

The Market Trends column now shows the recorded normalized PriceTrend value when available. **Positive PriceTrend (> 0) is green**, regardless of whether the trend was referenced by the selected setting; otherwise referenced trends are white and unreferenced trends are gray. A missing PriceTrend does not become zero or green. The Market Metrics column retains its separate gray/white/green trigger-result rules. This is a Monitor-only formatting update; no analyzer or exchange calculation changed. **Local build and focused checks pending** for this newer source.

## Global Settings Log — owner-directed two-column correction (November 10)

The Global Settings Log now uses **Market Trends** and **Market Metrics** as its only two market-data columns. The unrequested **Activation Triggers** column, Boolean expression, TRUE/FALSE text, and trigger diagnostics were removed **from the Monitor display**. Every configured MarketTrend appears in the Market Trends list: **white** if referenced in the selected GlobalSetting trigger set, **gray** otherwise. Every configured metric is displayed in the Market Metrics list, including asset-scoped metric names (without inventing a global value): **gray** if unused, **white** if referenced but not TRUE, **green** if an applicable trigger condition is TRUE. The source still retains historical trigger metadata for internal decision snapshots; analyzer decisions, PT configuration, and SOM are unaffected.

**Status:** source committed on the existing `phase1/market-data-foundation` branch; existing prior 337/337 validation applies only to `fb94373`, **not this newer UI correction**. A new local build/browser check is pending.

## SOM and Monitor changes — verified offline (November 10)

**User validation at exact source `fb94373d8928b681e3ebe1f7209bd1439db42e72`:** the .NET 10.0.403 Core/PTMagic/Monitor solution built successfully twice (15.6s, then 10.3s); all **16/16 offline harnesses and 337/337 test groups PASSED twice**. New Global Settings Log metric snapshot tests, SOM enable-only API and lifecycle tests, and two-raid red ticker assertions were included. The first run reported eight nonfatal existing nullable-source warnings in test projects; the second printed none. This is deterministic development acceptance only: actual Binance/ProfitTrailer API integration, configured runtime raids, and browser-visible Monitor behavior are still independently unverified.

The previous source-specific "not yet executed" statements below describe the **earlier state** when those features were published, and are **superseded for the now-verified source** by this result. Further Phase 2 work continues with retained funding and new taker-flow metrics, not positioning feeds. Source cleanup requires its own build evidence.

**Evidence:** [November 10 SOM and Monitor offline validation](https://github.com/HojouFotytu/PTM_2.10_Roadmaps_Handoffs/blob/main/updates/2026-11-10-som-monitor-offline-validation.md).

## Monitor — simpler metrics and complete Global Settings log (November 10)

The Market Metrics page no longer renders its **History Requirements** explanation/table. Analytical history requirements, actual collection, retention limits, completeness checks, warnings, and UNAVAILABLE behavior remain unchanged.

Every newly created Global Settings Log entry now stores an **activation-time snapshot** of all configured market-wide normalized metrics across all configured MarketTrends, including explicit unavailable values. White rows denote metrics referenced by the selected setting's trigger set **regardless of TRUE/FALSE/UNAVAILABLE outcome**; gray rows are not referenced. The separate Activation Triggers audit retains its Boolean expressions, bounds and states. Asset-only metrics are not mislabeled as global aggregates. Historical entries without metric snapshots are not backfilled from a newer raid, and preserve their legacy display where available. Retained DEFAULT entries without evaluated matching triggers correctly show no highlighted metrics. No changes to regime-selection logic, trading commands, SOM, data acquisition, or storage retention.

**Source checkpoint:** `f95ace1a0d8300cb63becf04e6a774439b505f71` on existing application branch `phase1/market-data-foundation`; new snapshot helper, summary record, selection-time capture and both Monitor view updates; two deterministic regression groups appended to `ConfigurationLifecycle`. **Status:** source published, local .NET build, full offline regressions, and browser check **not yet executed**.

## Startup global SOM — Enable-only safety and two-raid warning (November 10)

PTMagic startup global SOM is now **enable-only**: when StartupSellOnlyMode is configured and initial bootstrap requires the guard, it sends `enabled=true` and never sends `enabled=false`. The user manually disables SOM in ProfitTrailer when ready to resume buying. The API helper refuses all automatic disable requests. Successful normal settings delivery is independent of global SOM release. Legacy v1 PendingRelease checkpoints migrate to v2 manual review and cannot disable SOM. Uncertain enable blocks unsafe bootstrap; TestMode sends no PT command.

The first two startup RAID SUMMARY reports warn about manual SOM release. A red `PT: SOM` top Monitor ticker next to Selected Global Setting, SMS and PT health appears only on those first two raids. Its information tooltip describes the enable-only operation, manual release and the fact the indicator does **not** read the actual global SOM state. The ticker disappears after raid 2; it does not turn SOM off. SettingsGeneral help text and Status Summary metadata are updated accordingly.

**Published source:** `891f709010c867259a6cde3df8e9c82781091b7f`. Updated lifecycle regression tests and a source-level Monitor ticker assertion are authored; **no .NET build or new regression execution is yet claimed**. Prior 332/332 PASS is on the earlier source `49f2aab`. This supersedes the prior automatic-release behavior documented in D-025/D-037.

## Phase 2 — Retained funding validation (November 10–12)

Funding acquisition, cache restoration, exact settlement math and bulk/fallback behavior were exercised in previous offline runs. The full 18-harness suite passed at `d101b5a0` before D-053; this is historical pre-removal evidence, not proof of the current source's compilation. Removed OI/positioning harness results are excluded from the active test inventory.

## Monitor — Global Settings activation triggers (November 10)

The Control page Global Settings Log now preserves activation-time evaluations for every trigger on the selected GlobalSetting: tag, MarketTrend, normalized metric name, observed value and unit, configured bounds, TRUE/FALSE/UNAVAILABLE outcome, and the Boolean trigger connection. FALSE results are preserved because negation can make them part of a matching expression. The legacy Market Trends column remains available. Selection-basis labels distinguish matched regimes from DEFAULT fallback, indeterminate retention and flood protection. Existing history rows without this data are explicitly labeled; later measurements are never substituted for activation values. This changes Monitor/history reporting only, not trigger decisions or ProfitTrailer configuration. No local build or browser check is claimed for this change.

## Phase 2 — Funding-rate source and metric integration (November 10)

Historical settled Binance USD-M funding has been added to the optional normalized analyzer: a typed `fundingRate`/timestamp/`rateType`/markPrice mapper, the pinned `/fapi/v1/fundingRate` history client and a bounded sequential incremental service tracking verified query-window completeness. There is **no fixed storage-hours period or assumed 8-hour settlement cadence**. The full data horizon is derived from funding-enabled MarketTrends; `FundingMaxRequestsPerRaid` (default 60, 0–400) only caps requests.

Two asset-scoped metrics, `FundingRate` (latest regular settled rate in the window) and `FundingSum` (sum of regular settled rates), use percentage points without annualization. Special funding remains separate; unknown rateType does not become a fabricated regular zero. Missing/unverified data stays unavailable. Binance Spot/US never request Futures funding. Existing metric/triggers/exports/Monitor history paths are reused. Funding rate schedule adjustment metadata and Phase 2B aggregates remain future work.

Seventeen funding tests were authored and subsequently **passed 17/17** in the complete 16-harness run at `49f2aab` (332/332 total), with a successful .NET solution build. Earlier OI-only 315/315 results remain correctly attributed to source `5f51faaa`; the funding implementation was originally published at `6430bab1`.

## Release overview

PTMagic 2.10 adds a shared normalized market-data foundation, 26 selectable metric names, explicit data readiness, three-valued trigger evaluation, acknowledged configuration delivery, native startup global Sell Only Mode (SOM), safer Monitor editing, and bounded decision history. Binance Spot, Binance Futures and Binance US use the normalized analysis path. Existing PriceTrend configurations use that path too.

The foundation began at [`4153fb8`][foundation], before the application was renamed 2.10. Completed 2.9 framework, HTTP and performance modernization is the inherited baseline. The changes below concern the 2.10 application and its configuration-management behavior.

## 1. Shared market-data foundation and analytical populations

### Normalized observations and acquisition

- Introduced PTMagic-owned five-minute OHLCV observations with UTC candle times, OHLC prices, base/quote volume, trade count and closed-candle status. Malformed timestamps, invalid OHLC geometry, nonfinite/nonpositive prices and invalid volumes are rejected before analysis.
- Added a bounded in-memory store with ordered, deduplicated series. Refreshes reuse complete current windows, retrieve missing intervals and repair short or gapped history. Calculators consume these stored observations rather than issuing their own exchange requests.
- Unified the supported `Binance`, `BinanceFutures` and `BinanceUS` paths. Current ticker prices and 24-hour volumes remain separate inputs for market ordering and applicable price/age/volume conditions.
- Pinned a single UTC analysis time before normalized acquisition. Request bounds, retained observations and calculations honor that same pin even when a long initial load crosses a candle boundary.
- Kept unavailable measurements explicit: missing, invalid, stale or insufficient history does not become a numeric zero, a shortened lookback or a nearest-time substitute.

### Persistent normalized OHLCV warm start

- Added a versioned, per-exchange, per-symbol compressed disk cache for **completed normalized five-minute OHLCV candles** under `_data/NormalizedOhlcvCache`. The active runtime restores validated observations before its normal pinned refresh, so a short restart can fetch only the missing candles plus a small overlapping window rather than rebuilding the complete normalized history. Ordinary incomplete in-memory series retain conservative full backfill.
- Snapshots have normalized exchange/market identity, a strict candle-count bound, UTC-aligned timestamp and OHLCV validation, a complete-file marker, and gzip integrity checks. Cache files are written to a temporary path and atomically replaced. A corrupt, missing, incompatible or incomplete cache never makes unavailable metrics valid: the normal refresh requests missing/full history and keeps its existing readiness rules.
- Files are bounded by `StoreDataMaxHours`/per-series capacity when restored, and saves are throttled to avoid rewriting each market on every raid. Cache I/O failures produce warnings, not false trade or configuration acknowledgements.
- This persistent cache applies **only to normalized OHLCV**. The separate legacy market-tick reconstruction, first-seen data and older MarketData files are unchanged.
- A new eight-group offline regression harness exercises restoration, incremental restart, corruption, gaps, exchange isolation, retention and write throttling. **Verified November 9 at source `925db7c`:** .NET SDK 10.0.403 solution build succeeded in 18.3 seconds; all 8 cache, 16 analytics-reliability and 13 Phase 1C OHLCV tests passed (37/37). The older 272-check inventory was executed on earlier source revisions; configured two-startup cache reuse and unchanged live metrics remain unverified.


Sources: [observation store][store], [refresh service][service], [shared exchange runtime][runtime] and [snapshot models][snapshots].

### Frozen populations and readiness

Each raid freezes a copied, normalized analytical universe after PT-dependent settings resolve. The snapshot retains relevant Futures contract/underlying metadata and keeps analytical eligibility separate from the broader exchange candle cache.

The new engine applies the existing underlying-type and pair-filter controls. When `UseProfitTrailerEnabledPairs` is enabled, both `enabled_pairs` and `disabled_pairs` are honored consistently, accepting coin names or full market symbols. These controls themselves predate 2.10; the frozen population and consistent exclusions are the changes recorded here.

For each MarketTrend, allowed/ignored markets determine eligibility. The global population applies `MaxMarkets` to the current 24-hour ticker-volume ranking; the per-asset population is not automatically reduced by that cap. Valid samples are counted against the exact selected eligible population, including members whose observations are missing.

General aggregate readiness requires a nonempty population and at least `ceil(0.90 × eligible count)` valid samples. Return `Concentration` and the complete-volume HHI/share metrics require 100% coverage. Values retain sample counts, eligible counts, coverage, source timestamps and reasons when unavailable.

Sources: [immutable universe][universe], [population selection][population], [coverage policy][coverage] and [PT pair resolution][settings-handler].

### Retained history requirements

The existing `MarketAnalyzer.StoreDataMaxHours` now bounds normalized retention. Metric diagnostics distinguish minimum complete observations, slots required at the current raid alignment, conservative retention for every UTC alignment, configured capacity and unmet dependencies. Insufficient retention makes the affected measurement unavailable and explains the requirement; PTMagic does not silently enlarge retention or shorten the requested calculation.

The normalized store remains in memory. Persistent files described later contain inspection and decision records, not a durable candle archive. Source: [history-requirement calculations][history-requirements].

## 2. Current metric catalogue

The registry exposes **24 canonical metric names** (22 normalized metrics, including three correlation metrics, and 2 settled-funding metrics). “Global” means a market-wide measurement usable by a GlobalSetting; “asset” means a market-specific measurement usable in SMS activation/off-trigger evaluation. Scope is checked when a trigger requests a metric.

### Returns and relative strength

| Metric | Scope | Definition and unit |
|---|---|---|
| `PriceTrend` | Global, asset | Asset simple return; global arithmetic mean of selected valid returns, subject to the PriceTrend-only outlier rule. Percent. |
| `MedianPriceTrend` | Global | Median selected valid return. Percent. |
| `MeanMedianSpread` | Global | Unfiltered arithmetic mean return minus median return. Percentage points. |
| `Breadth` | Global | Percentage of valid selected returns strictly above zero. |
| `Dispersion` | Global | Population standard deviation of selected returns. Percentage-point scale. |
| `RelativeStrengthMarket` | Asset | Asset return minus the global median return. Percentage points. |
| `RelativeStrengthBTC` | Asset | Asset return minus the aligned BTC return. Percentage points. |
| `Concentration` | Global | Mean return of the Top 10 ranked by quote volume minus the mean return of the remaining population. Percentage points. |

Return metrics require the exact latest fully closed five-minute endpoint defined by the raid pin and the exact endpoint `TrendMinutes` earlier; either endpoint missing makes the return unavailable. `Concentration` ranks volume over that same trailing `TrendMinutes` window and endpoint, requires at least eleven markets and requires complete eligible data.

Sources: [canonical names and scopes][metric-registry] and [configured return calculations][return-engine].

### Volatility and volume

| Metric | Scope | Definition and unit |
|---|---|---|
| `ATRP` | Asset | Wilder ATR divided by the latest completed trend-bar close, multiplied by 100. Percent. |
| `MedianATRP` | Global | Median valid asset ATRP. Percent. |
| `VolatilityBreadth` | Global | Percentage of valid ATRP values above the configured threshold. |
| `VolumeExpansion` | Asset | Latest completed trend-period quote volume divided by the mean of the preceding N completed periods. Ratio. |
| `MedianVolumeExpansion` | Global | Median valid volume-expansion ratio. |
| `VolumeExpansionBreadth` | Global | Percentage of valid expansion ratios above the configured threshold. |
| `StrongVolumeExpansionBreadth` | Global | Percentage above the configured strong-expansion threshold. |
| `VolumeConcentration` | Global | Sum of squared eligible quote-volume fractions, multiplied by 100. HHI index. |
| `TopNVolumeShare` | Global | Top N markets' combined share of complete eligible period quote volume. Percent. |
| `BTCVolumeShare` | Global | BTC share of complete eligible period quote volume. Percent. |
| `BTCETHVolumeShare` | Global | Combined BTC and ETH share of complete eligible period quote volume. Percent. |

These calculations aggregate closed five-minute inputs into **complete UTC-aligned `TrendMinutes` bars**. `ATRP` is the public metric name; ATR is its internal calculation. ATRP needs at least `ATRPeriod + 1` contiguous complete bars, seeds true ranges at the oldest available contiguous retained window and Wilder-smooths forward. Volume expansion requires the latest complete period plus the preceding N periods; a zero or invalid baseline is unavailable.

The share metrics do not invent a zero for an absent required BTC/ETH population member. `TopNVolumeShare` is unavailable when fewer than N eligible markets exist. `Concentration` and `VolumeConcentration` have different definitions: the former compares returns, while the latter measures volume HHI.

Sources: [OHLCV aggregation and formulas][period-metrics] and [global volatility/volume calculations][ohlcv-engine].

### Binance USD-M Futures settled funding

| Metric | Scope | Definition and unit |
|---|---|---|
| `FundingRate` | Asset | Latest Regular settled funding rate in the configured window, percentage points. |
| `FundingSum` | Asset | Sum of Regular settled rates over the configured window, percentage points. |

Funding is opt-in and Binance USD-M Futures-only. Its native history remains independent of normalized OHLCV retention, stored in a versioned disk cache, and acquired bulk-first with safe conditional per-symbol fallback. Unavailable/unverified source observations are not interpreted as zero.

### BTC correlation

| Metric | Scope | Definition and unit |
|---|---|---|
| `CorrelationToBTC` | Asset | Pearson correlation of synchronized asset and BTC simple-return vectors. Coefficient from -1 to +1. |
| `MeanCorrelationToBTC` | Global | Mean valid selected BTC correlation coefficient. |
| `MedianCorrelationToBTC` | Global | Median valid selected BTC correlation coefficient. |

Correlation uses complete UTC-aligned trend periods. N returns require N+1 exact endpoints and complete source continuity. Missing, shifted, nonfinite or constant vectors are unavailable; the calculator does not fill gaps or shorten overlap. BTC self-correlation is +1 only when its actual vector is valid and nonconstant. Global mean/median correlation uses the selected global population and the 90% readiness rule.

Sources: [synchronized correlation calculator][correlation-calculator] and [correlation integration][correlation-engine].

### Metric selection and PriceTrend compatibility

- `MarketTrend.Metrics` accepts comma-separated, case-insensitive names. An omitted or blank list enables normalized `PriceTrend`.
- An omitted activation/off-trigger `Metric` means `PriceTrend` and adds that implicit dependency to the referenced trend. An explicitly requested disabled metric stays unavailable; an unknown name stays diagnostic.
- Internal dependencies do not automatically expose unrelated metrics for triggers.
- Global PriceTrend uses the global population. SMS `Relative` PriceTrend uses a separate uncapped asset-population arithmetic mean. Existing `Absolute`, `Relative` and `RelativeTrigger` relations resolve through normalized inputs, including retained activation-relative state.
- `TrendThreshold` applies only to arithmetic PriceTrend means, including the SMS-relative reference. When nonzero, it retains returns whose absolute value is at most the threshold's absolute value. It does not filter per-asset returns or change the populations or formulas of the other metrics.
- `TrendCurrency=Fiat` compounds synchronized asset/quote and quote/USDT return factors on the same exchange. USD/USDT use identity conversion. Missing conversion history stays unavailable, and an internally required conversion pair does not automatically join the eligible analytical population.
- Fiat conversion applies to return-derived inputs and correlation vectors. OHLC/quote-volume metrics use native quote OHLCV.
- Exchange `TrendMinutes` must be positive and an exact multiple of five minutes. Return endpoints and completed trend-bar endpoints follow their respective rules above, so different metric families can legitimately show different source times.

Sources: [metric selection][metric-selection], [trigger resolver][metric-resolver], [Fiat conversion][fiat-conversion], [timeframe validation][timeframe] and [return engine][return-engine].

## 3. Trigger evaluation and SMS lifecycle

GlobalSetting, SMS activation and SMS off-trigger evaluation distinguish **true, false and unavailable**. Negation preserves unavailable. Conclusive Boolean branches remain conclusive: `true OR unavailable` is true, and `false AND unavailable` is false. Tagged custom activation expressions use the same semantics; off-trigger connections retain the supported AND/OR contract.

When no global candidate conclusively matches and unresolved inputs remain, PTMagic retains the existing setting instead of treating missing evidence as a DEFAULT match. A conclusive independent match can still be selected. Unknown activation cannot newly activate an SMS, and it retains an already-applied SMS while its activation is unavailable. Persistent SMS removal requires a conclusive true off-trigger expression.

Age/time and volume observations have explicit unavailable states for missing, future, sentinel or invalid data. Time/volume-only rows do not require a MarketTrend reference. Metric-based rows do. Each SMS receives an independent copy of its activation descriptions, so combined market comments cannot mutate another setting's persisted trigger summary.

Removing the final active SMS is treated as a configuration change even if the GlobalSetting name stays the same. The clean global payload must be delivered to remove remote overrides. Removed settings are pruned from SMS persistence, and TestMode uses its own `SingleMarketSettingSummary.TestMode.json` rather than replacing the live SMS latch.

Sources: [three-valued evaluator][trigger-logic], [raid and setting orchestration][orchestration], [independent SMS summaries][sms-summary] and [lifecycle regression coverage][lifecycle-tests].

## 4. Configuration validation, compilation and acknowledged delivery

### Structural validation and preset bootstrap

Startup and Monitor saves validate trend/setting identity, references, tags, expressions, supported timeframes and parameters required by active metrics. Blank or duplicate identities, unresolved metric-row references and malformed expressions are rejected. Simple AND/OR activation does not require tags; custom expressions require usable, unique tags. The fallback contract requires a GlobalSetting name containing `default`, case-insensitively. `OffTriggers=null` continues to mean no persistent off-condition.

Unknown, explicitly disabled or wrong-context metric requests produce warnings and unavailable measurements. This remains distinct from a broken configuration structure and permits intentional diagnostic settings.

Null PAIRS, DCA or INDICATORS override groups inherit the default preset's assignments intact. Before exchange loading, PTMagic resolves the quote currency from the exact compiled PAIRS `market` key, normalizes its asset code and rejects missing, blank or malformed values locally. Valid non-USDT quotes are preserved. All three supported adapters return the USDT identity price without a ticker request; Binance US additionally handles its USD identity case.

Sources: [structural validator][validator], [property compilation and quote parsing][settings-handler] and [Binance currency handling][currency-handler].

### Delivery state and recovery

- Separate desired configuration, pending delivery and confirmed applied configuration. Selection alone does not mark a PT update as applied.
- Compile the complete PAIRS/DCA/INDICATORS payload for all configured targets. Only acknowledged non-TestMode delivery advances the live confirmed state and applied-setting notification.
- Preserve the previously confirmed view after failed or partial delivery and retry an identical desired payload. Reloaded definitions and preset contents are recompiled even when the setting name is unchanged.
- Reconcile partial-target updates even if the desired configuration returns to an older confirmed hash. An old hash cannot suppress the required recovery replay.
- Skip redundant transmission only after acknowledgement of the unchanged payload, unless explicitly forced. Initial successful analysis confirms the normal configuration through delivery.
- Persist confirmed metadata in `_data/ConfigurationDeliveryState.json`: identities, hashes and acknowledgement time, without raw properties, tokens or licenses. Persistence errors after a real acknowledgement are reported without denying that acknowledgement.
- Require a completed, usable current raid before publication. Freshness is rechecked before each target/retry; missing, future or expired analysis defers delivery, including decisions made only from age/volume conditions.

Sources: [delivery coordinator][delivery], [PT settings transport][settings-api], [freshness gate][freshness] and [orchestration][orchestration].

## 5. Native startup global Sell Only Mode — enable-only (D-047)

`GeneralSettings.Application.StartupSellOnlyMode` defaults to `true`. At live first startup, PTMagic may request native ProfitTrailer global SOM ON:

```text
POST /api/v2/globalSellOnlyMode
query: token=<configured API token>&enabled=true
```

Global SOM stops new buys and DCA buys but allows sells. The control request does **not** upload a PAIRS, DCA or INDICATORS overlay. The configured PT Monitor base path, primary/additional target tokens, response validation and credential protections remain in effect.

**No automatic release.** PTMagic's native SOM helper rejects `enabled=false`; successful market analysis, normal configuration acknowledgement, restarting and opting out do not authorize an automatic OFF. Only the **user manually disables SOM in ProfitTrailer** when ready to resume buying. The native setter supplies no authoritative readback or user ownership, so neither PTMagic's local guard checkpoint nor the Monitor warning proves the current remote state.

PTMagic saves its intent before attempting `enabled=true` and records local acknowledgement/manual-review metadata separately from normal PT configuration delivery. `_data/StartupSellOnlyModeState.json` uses schema v2; legacy schema-v1 `PendingRelease` data migrates to manual review, never to a release operation. A failed/unknown response or partial multi-target acknowledgement must not be treated as successfully established protection. `StartupSellOnlyMode=false` prevents new automatic enables but does not clear existing remote protection. TestMode sends no native SOM API request or live checkpoint changes.

**Two-raid notification:** During startup raids 1 and 2, RAID SUMMARY logs a `PT: SOM WARNING` requiring manual release in ProfitTrailer. The shared Monitor header displays a **red `PT: SOM` ticker** beside Global Setting, SMS and PT health, with an information tooltip explaining the enable-only/manual-release policy. From raid 3, the temporary ticker and warning disappear. Their disappearance neither disables SOM nor proves it is OFF. Manual-review status stays available in Status Summary.

**Verification:** Latest application source `891f709010c867259a6cde3df8e9c82781091b7f` has been published but **NOT yet built/regression-tested** following these changes. Last verified `49f2aab66592b6e2922097ca5242b5a614090602` passed 332/332 offline groups before SOM revision. Real PT behavior and Monitor browser acceptance remain separate.

Sources: [SOM transport][som-api], [guard checkpoint][som-state], [startup orchestration][orchestration] and [current SOM decision D-047][som-decision].

### Unified TestMode console highlighting

All PTMagic and Monitor console messages containing `TestMode` (case-insensitive)—including startup warning, startup SOM previews, configuration-delivery previews and RAID SUMMARY lines—use the same **magenta/purple** foreground as the initial `TESTMODE ENABLED` warning. NLog `ColoredConsole` highlights these messages independent of INFO/WARN severity. The ordinary severity levels, file-log text, and coloring of unrelated messages do not change. The main PTMagic console, Monitor console and packaged default NLog configuration share this rule. The user's installed `nlog.config` must be updated for the running process to reflect it.

**Verification:** source/configuration review only at publication; .NET build and real console-color acceptance on the user's Windows environment remain pending. A color-rendering configuration is not proof of live PT delivery.

## 6. Monitor inspection and editing

The Market Metrics page and `MarketTrendAnalytics.json` expose the configured metrics with values, scope, units, selected populations, coverage, source endpoints, unavailable reasons and history requirements. Existing PriceTrend summaries and Market Analyzer views consume normalized outputs. Unavailable results stay blank or explicitly unavailable instead of becoming fabricated values or revived stale history.

Per-raid diagnostics group repeated metric issues while retaining every distinct warning group. They identify affected trends, metrics and callers, distinguishing disabled/unknown/wrong-context requests, history shortages, stale inputs and coverage failure. Return spreads are mathematically percentage points; some existing display helpers still use a `%` suffix. HHI, ratios and correlation coefficients have their own interpretation.

Analyzer editing now uses ordered row identities instead of treating the MarketTrend name as a row key. Multiple rows referencing the same trend retain their separate metric, tag, relation, threshold and position. Add/delete/reorder operations preserve mixed metric, age, volume and timed off-trigger rows.

No-edit saves preserve numerical precision, native property types, formulas, suffixes, reference spelling, inactive fields and unrendered advanced settings. Edits are applied to a detached copy and validated before an atomic file replacement. Revision tokens reject stale forms; incomplete loads, invalid/repeated row identities and repeated scalar values fail without mutating the source configuration.

Operational summaries distinguish the selected GlobalSetting from the confirmed applied setting and show delivery state, startup SOM state, analysis time and source revision. General Settings exposes Startup Global SOM and additional-target API tokens. The Startup Global SOM toggle is in **General Settings → APPLICATION**, directly below Test Mode, with visible help explaining that TestMode leaves the remote flag unchanged. Its on/off value is saved to `Application.StartupSellOnlyMode`.

Sources: [metrics page][metrics-page], [inspection export][monitor-export], [form processing][form-processing], [atomic settings store][settings-store], [status display][status-display] and [general-settings page][general-page].

## 7. Decision history and build identity

Added bounded JSONL decision history under `_data/DecisionHistory/YYYY-MM-DD-NNNNNN.jsonl`. Records identify the raid, version/source, analysis and decision times, configuration and desired/confirmed payload hashes, selected/confirmed settings, SMS identities, evaluated inputs and availability, analytical populations, freshness, TestMode, SOM and delivery outcomes. Relative-trigger inputs remain separately attributable.

Failed raids record safe status/error categories once the required services are initialized. Records exclude raw general settings, raw PT properties, tokens/licenses, HTTP text and unrestricted exception strings. History append errors do not change an already-known delivery result.

Segments rotate at 16 MiB. Unified age-based `HistoryRetentionDays` pruning removes whole expired segments; the previous cumulative 256 MiB quota and size-based early deletion have been removed. Interrupted tails cause a new segment, and a single oversized record is rejected before damaging history. This is the implemented decision audit trail. It records decisions and evidence, without claiming strategy-performance or full historical replay validation.

Core embeds the Git source revision at build time. Runtime logs, summary, Monitor and decision history expose it, with `-dirty` for detected uncommitted changes and `unknown` if identity is unavailable. Version `2.10.0` alone does not identify which implementation is running.

Sources: [history storage/capture models][decision-history], [source-identity build target][core-project] and [raid capture][orchestration].

## 8. Settings and upgrade notes

### General settings

These fields belong in **`settings.general.json` → `GeneralSettings.Application`**.

| Field | Default | Effective behavior |
|---|---|---|
| `StartupSellOnlyMode` | `true` | Enable-only native global SOM at startup, followed by manual release in ProfitTrailer; no automatic OFF. Explicitly present in both supplied general-settings templates. |
| `ProfitTrailerServerAPITokenXtra` | `""` | Blank reuses the primary token. Otherwise comma-separated tokens correspond to additional PT URLs in the same order. |
| `MaxAnalysisAgeSeconds` | `300` | Permitted range 1–86400 seconds. Defines an analysis freshness deadline; the optional FAILSAFE or provisional best-available path can deliver after this expires. |
| `DecisionHistoryEnabled` | `true` | Enables segmented decision records; there is no cumulative size quota. |
| `HistoryRetentionDays` | `30` | Unified disk retention (1–3650 days) for legacy MarketData snapshots and DecisionHistory, separate from active OHLCV candle capacity. Migrates explicitly configured legacy `DecisionHistoryRetentionDays` if the new key is absent. |
| `TestMode` | `true` | Existing setting; 2.10 extends isolation to native SOM, acknowledgement checkpoints and SMS persistence. |

The supplied files are [`PTMagic/_defaults/_default_settings_PT_2.x/settings.general.json`][packaged-settings] and [`_Development/DevSettings/settings.general.json`][development-settings]. Both explicitly contain `"StartupSellOnlyMode": true` under `Application`. Existing installations obtain the default when the field is absent; a configured `false` prevents a new enable but never releases an existing global SOM flag.

### Analyzer and metric settings

These fields belong on the appropriate MarketTrend in **`settings.analyzer.json` → `AnalyzerSettings.MarketAnalyzer.MarketTrends`**.

| Field | Default | Meaning |
|---|---|---|
| `Metrics` | `""` | Blank selects normalized PriceTrend; explicit names select the requested outputs. |
| `ATRPeriod` | `14` | True-range seed period for Wilder ATRP. |
| `VolumeExpansionLookback` | `20` | Prior completed periods in the quote-volume baseline. |
| `ATRPBreadthThreshold` | `0.5` | ATRP percentage threshold. |
| `VolumeExpansionBreadthThreshold` | `1.0` | Expansion-ratio threshold. |
| `StrongVolumeExpansionThreshold` | `1.5` | Strong expansion-ratio threshold. |
| `TopNVolumeCount` | `10` | Number of markets in TopNVolumeShare. |
| `CorrelationLookback` | `30` | Number of synchronized trend-period returns. |

For active relevant metrics, ATR/lookback/Top-N counts accept 1–1000 and correlation accepts 3–1000. The three metric-specific breadth/expansion thresholds in this table must be finite and nonnegative when active. Unused parameter fields do not invalidate otherwise compatible configurations. Trigger and off-trigger rows select measurements using their `Metric` field. Sources: [settings definitions][settings-model] and [active-parameter validation][validator].

### Compatibility actions

- Exchange analysis now uses the normalized PriceTrend path throughout. CoinMarketCap ingestion, its API-key/source controls, obsolete Bittrex/Poloniex analyzers and the separate legacy PriceTrend calculators have been removed. Existing CMC trends need an Exchange configuration; removed source behavior is not an available fallback. [Removal scope][source-cleanup].
- Use positive five-minute-multiple Exchange trend durations and enough `StoreDataMaxHours` for the selected metrics. Check reported current/all-alignment requirements, especially for longer trend periods.
- Confirm the runtime's referenced preset filenames exist and its compiled PAIRS contains the intended `market`. Preset validation runs in TestMode too, before the startup guard and market-data load.
- Check both `TestMode` and `StartupSellOnlyMode` when assessing startup protection. Enabling live operation also permits ordinary PT configuration delivery; TestMode remains a simulation even when the startup option is true.
- Deploy a coherent application/Monitor bundle and verify the runtime source revision. Preserve instance settings, presets, data and logs. A Git checkout, successful build or working directory by itself does not identify an installed process's loaded binaries.

## 9. Validation evidence and remaining acceptance

User-executed builds with .NET SDK 10.0.403 succeeded for Core, PTMagic and Monitor. The current deterministic evidence totals **272 passing checks across twelve harness projects, collected over two source revisions**:

| Harness | Passing checks | Executed source |
|---|---:|---|
| `Phase1AReadiness` | 5 | `86c2ea3` |
| `Phase1COhlcv` | 13 | `86c2ea3` |
| `Phase1SmsSummaryIsolation` | 5 | `86c2ea3` |
| `Phase1TriggerLogic` | 29 | `86c2ea3` |
| `NormalizedPriceTrendMigration` | 18 | `86c2ea3` |
| `Phase1MetricWarnings` | 18 | `86c2ea3` |
| `Phase1AnalyticsReliability` | 16 | `86c2ea3` |
| `Phase1DCorrelation` | 21 | `86c2ea3` |
| `TriggerConfigurationValidation` | 40 | `86c2ea3` |
| `MonitorSettingsRoundTrip` | 28 | `86c2ea3` |
| `ConfigurationLifecycle` | 66 | `d331fbd` |
| `DecisionHistory` | 13 | `d331fbd` |
| **Total** | **272** | **193 + 79 across the two runs** |

Full execution identities are `86c2ea3ff430b62e654f929a222935ec53ea8fa6` and `d331fbd059041241069646c3bb82ac6549423a70`. Production source and test assertions were unchanged between these runs. `fac346c88b2c634439083e7515193d756ac3d186` records the evidence in test READMEs. These results are not represented as a new twelve-project run at a later documentation/template commit. [Recorded execution evidence][validation].

Configured runtime acceptance remains distinct: current-binary completed raids, actual PT configuration/SOM acknowledgement and recovery, Monitor browser save/reload, slow-load freshness/retention behavior, configured correlation/history inspection, non-USDT Fiat conversion, and Binance Spot/US runtime parity. Fake-transport tests establish orchestration behavior but do not establish the remote global SOM flag or a live trading outcome. The latest reported long TestMode run does not establish live SOM activation.

## 10. Maintenance for pull requests and merges

1. Update this file with every 2.10 PR that changes effective behavior, metrics, settings, compatibility, diagnostics or validation. Describe the resulting behavior and the reason it matters.
2. Keep the application and roadmap/handoff copies identical as part of the same development handoff. Include this file in application merge contents and reference its relevant sections in PR, merge and release descriptions.
3. Consolidate or remove an entry when it is superseded or reversed. Do not append an interim chronology to this record; dated project history and source control retain that provenance.
4. Keep execution evidence attached to the exact source actually tested. Distinguish implementation, deterministic execution, configured runtime and browser acceptance.
5. Update settings paths/defaults and source links alongside the behavior they describe. Keep future work in the roadmap and personal strategy work outside the application release record.

[app-copy]: https://github.com/HojouFotytu/PTMagic/blob/phase1/market-data-foundation/CHANGELOG-2.10.md
[docs-copy]: https://github.com/HojouFotytu/PTM_2.10_Roadmaps_Handoffs/blob/main/CHANGELOG-2.10.md
[roadmap]: https://github.com/HojouFotytu/PTM_2.10_Roadmaps_Handoffs/blob/main/roadmaps/CURRENT.md
[status]: https://github.com/HojouFotytu/PTM_2.10_Roadmaps_Handoffs/blob/main/STATUS.md
[foundation]: https://github.com/HojouFotytu/PTMagic/commit/4153fb83786d8e9812b4fbc7def1b9f0ba747dbb
[store]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/MarketData/MarketDataStore.cs
[service]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/MarketData/MarketDataService.cs
[runtime]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/MarketData/BinanceFuturesAnalyticsRuntime.cs
[snapshots]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/MarketData/AnalysisSnapshots.cs
[universe]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/MarketData/AnalyzerUniverseSnapshot.cs
[population]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/MarketData/MarketTrendPopulationSelector.cs
[coverage]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/MarketData/MarketMetricCoverage.cs
[history-requirements]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/MarketData/MarketTrendHistoryRequirements.cs
[metric-registry]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/MarketData/MarketTrendMetrics.cs
[return-engine]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/MarketData/MarketTrendAnalyticsEngine.cs
[period-metrics]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/MarketData/TrendPeriodMetricsCalculator.cs
[ohlcv-engine]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/MarketData/MarketTrendPhase1CEngine.cs
[correlation-calculator]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/MarketData/SynchronizedReturnCorrelationCalculator.cs
[correlation-engine]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/MarketData/MarketTrendPhase1DEngine.cs
[metric-selection]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/MarketData/MarketTrendMetricSelection.cs
[metric-resolver]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/MarketData/MarketTrendMetricTriggerResolver.cs
[fiat-conversion]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/MarketData/MarketTrendFiatConversion.cs
[timeframe]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/MarketData/MarketTrendTimeframe.cs
[trigger-logic]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/MarketData/ThreeValuedTriggerLogic.cs
[orchestration]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/Main/PTMagic.cs
[sms-summary]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/Helper/MatchedMarketTriggerContent.cs
[lifecycle-tests]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Tests/ConfigurationLifecycle/README.md
[validator]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/Helper/TriggerConfigurationValidator.cs
[settings-handler]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/ProfitTrailer/SettingsHandler.cs
[currency-handler]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/BinanceAnalyzerCommon.cs
[delivery]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/ProfitTrailer/ConfigurationDelivery.cs
[settings-api]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/ProfitTrailer/SettingsAPI.cs
[freshness]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/MarketData/AnalysisDecisionFreshness.cs
[som-api]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/ProfitTrailer/GlobalSellOnlyModeAPI.cs
[som-state]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/ProfitTrailer/StartupSellOnlyModeState.cs
[som-decision]: https://github.com/HojouFotytu/PTM_2.10_Roadmaps_Handoffs/blob/ad4029c858931384206413c966ce737f356d7623/DECISIONS.md#d-037--direct-global-som-api-supersedes-the-startup-preset-overlay
[metrics-page]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Monitor/Pages/MarketMetrics.cshtml
[monitor-export]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/MarketAnalyzer/MarketData/MarketTrendAnalyticsMonitorExporter.cs
[form-processing]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Monitor/Configuration/AnalyzerSettingsForm.cs
[settings-store]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Monitor/Configuration/AnalyzerSettingsStore.cs
[status-display]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Monitor/Pages/StatusSummary.cshtml
[general-page]: https://github.com/HojouFotytu/PTMagic/blob/phase1/market-data-foundation/Monitor/Pages/SettingsGeneral.cshtml
[decision-history]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/Helper/DecisionHistory.cs
[core-project]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/Core.csproj
[settings-model]: https://github.com/HojouFotytu/PTMagic/blob/fac346c88b2c634439083e7515193d756ac3d186/Core/DataObjects/PTMagicData.cs
[packaged-settings]: https://github.com/HojouFotytu/PTMagic/blob/phase1/market-data-foundation/PTMagic/_defaults/_default_settings_PT_2.x/settings.general.json
[development-settings]: https://github.com/HojouFotytu/PTMagic/blob/phase1/market-data-foundation/_Development/DevSettings/settings.general.json
[source-cleanup]: https://github.com/HojouFotytu/PTM_2.10_Roadmaps_Handoffs/blob/ad4029c858931384206413c966ce737f356d7623/updates/2026-11-04-cmc-legacy-pricetrend-cleanup.md
[validation]: https://github.com/HojouFotytu/PTM_2.10_Roadmaps_Handoffs/blob/ad4029c858931384206413c966ce737f356d7623/updates/2026-11-07-completed-harness-validation.md
