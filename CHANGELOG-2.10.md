# PTMagic 2.10 — Cumulative release changes

**Updated:** November 10, 2026

**Application version:** 2.10.0, in development

**Application branch:** `phase1/market-data-foundation`

This is the cumulative record of **2.10 changes that remain in effect**. It covers the normalized market-analysis foundation begun on November 2, 2026, through the current implementation. Entries describe the resulting behavior and are consolidated when that behavior changes. Planned features, superseded implementations, reversed changes and personal trading-strategy work are outside this record.

The same document is maintained in the [application repository][app-copy] for code merges and pull requests, and in the [2.10 roadmap/handoff repository][docs-copy] for project continuity. The [current roadmap][roadmap] tracks future work; [status and evidence][status] track acceptance. The implementation references below identify the inspected `fac346c` source baseline. The accompanying template and Monitor help updates make the existing startup SOM default explicit in both supplied general-settings files and explain its TestMode behavior beside the toggle.

## Phase 2 — Incremental open-interest collector and metric integration (November 10)

Published application source `b723f76664aa0de6b76de3f5ddf281c036c32275` integrates the optional Binance USD-M historical OI source into the existing normalized analyzer. New `OpenInterestChange` and `OpenInterestValueChange` asset metrics use exact historical 5m timestamps over configured `TrendMinutes` and preserve separate native quantity and provider-reported value semantics. Missing or inconsistent source observations remain unavailable. No metric is enabled implicitly.

The bounded per-symbol collection uses the existing shared Binance HTTP limiter, incremental pages, request fairness, and a versioned persistent cache. The OI data horizon is derived from the longest OI-enabled user MarketTrend's `TrendMinutes`, plus its exact reference/overlap, with no independent hours setting or 672-hour software cap. `MarketAnalyzer.OpenInterestMaxRequestsPerRaid` (default 60; allowed 0–400) remains a separate request throttle, and zero disables acquisition. The request cap is 500 source rows per page, up to two pages per selected market in one raid. No Futures OI is collected on Binance Spot or Binance US.

The existing metric registry, normalized engine, trigger resolver, history requirements, runtime logs, MarketTrendAnalytics export and Monitor metric snapshot are extended through the current shared path. The Monitor History Requirements view distinguishes dynamically derived OI history from OHLCV StoreDataMaxHours. The previous default-72h OI setting was removed at `0ae76f4c7886397a06ad346b42a375f6aa06be00`; unavailable provider history is not silently truncated. Existing configurations without an explicit OI metric perform no additional OI API requests. A new 15-group offline integration harness was authored; the original 18 raw OI offline cases remain. **No .NET build, local harness or live integration acceptance has yet been executed for this checkpoint**, per the owner's test deferral. This is published source only, not a new verified release.

## Phase 2 — First open-interest source foundation (November 10)

At application commit `314214b1a20bcddd1643b8fa8df032db5463b04d`, optional Binance USD-M futures current and historical open-interest observation models and a bounded single-page HTTP client were added. The new model preserves original native quantity and reported value separately, rejects invalid source symbols/timestamps/amounts, and calculates percentage change only from exact five-minute historical endpoints. Source can be requested explicitly by callers and uses PTMagic's established HTTP/rate-limit/retry transport.

**This release increment does not register new MarketTrend metrics, start background acquisition, change existing raids, or alter PT configuration behavior.** The 18 new offline test groups are authored but **not executed**, and no .NET build was performed on the published commit in this turn. Phase 1 configured acceptance remains deferred by owner instruction and was not reclassified as passing.

## Release overview

PTMagic 2.10 adds a shared normalized market-data foundation, 22 selectable metric names, explicit data readiness, three-valued trigger evaluation, acknowledged configuration delivery, native startup global Sell Only Mode (SOM), safer Monitor editing, and bounded decision history. Binance Spot, Binance Futures and Binance US use the normalized analysis path. Existing PriceTrend configurations use that path too.

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

The registry exposes **22 canonical metric names**. “Global” means a market-wide measurement usable by a GlobalSetting; “asset” means a market-specific measurement usable in SMS activation/off-trigger evaluation. Scope is checked when a trigger requests a metric.

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

## 5. Native startup global Sell Only Mode

`GeneralSettings.Application.StartupSellOnlyMode` defaults to **`true`**. After configuration/preset validation and before the long market-data load, PTMagic sends the native ProfitTrailer control request to every configured target:

```text
POST /api/v2/globalSellOnlyMode
query: token=<configured API token>&enabled=true
```

Global SOM blocks new buys and DCA buys while allowing sells. This request controls the global flag and carries no PAIRS, DCA or INDICATORS payload. PTMagic preserves configured URL context paths and uses the appropriate primary/additional target tokens. The control transport validates target/token alignment before requests, rejects redirects, login pages and explicit API failures, bounds response handling and avoids exposing token-bearing request failures.

PTMagic records its pending control intent before sending the enable request. Enable or intent-persistence failure defers bootstrap. Release uses `enabled=false` only after a **fresh, conclusive, completed raid and acknowledged normal configuration delivery to every target**. Failed collection, indeterminate results, stale analysis or failed delivery retain the guard. Freshness is checked again before release and between targets.

Release has separate acknowledgement and retry state. A failed release preserves the normal configuration acknowledgement and leaves the owned operation pending; the next cycle reasserts the guard and retries release after fresh conclusive analysis. `_data/StartupSellOnlyModeState.json` stores metadata for restart recovery without credentials or properties.

**TestMode behavior:** `TestMode=true` simulates guard enable, normal configuration delivery and guard release. It sends none of these PT-changing API requests and does not overwrite live acknowledgement checkpoints. A long TestMode load therefore does not itself enable remote SOM.

**Flag ownership:** enabling startup SOM gives PTMagic control of that switch for its startup cycle. There is no previous-value readback or restoration; a manually pre-enabled SOM can later be set false when release conditions pass. Disabling the option prevents new startup control, while a matching checkpoint still resumes an already-owned pending operation. Command acknowledgement is tracked; it is not a separate readback of the live PT flag.

Sources: [SOM transport][som-api], [guard checkpoint][som-state], [startup orchestration][orchestration] and [accepted endpoint/ownership contract][som-decision].

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

Segments rotate at 16 MiB. Retention and total quota prune whole older files; interrupted tails cause a new segment, and a single oversized record is rejected before damaging history. This is the implemented decision audit trail. It records decisions and evidence, without claiming strategy-performance or full historical replay validation.

Core embeds the Git source revision at build time. Runtime logs, summary, Monitor and decision history expose it, with `-dirty` for detected uncommitted changes and `unknown` if identity is unavailable. Version `2.10.0` alone does not identify which implementation is running.

Sources: [history storage/capture models][decision-history], [source-identity build target][core-project] and [raid capture][orchestration].

## 8. Settings and upgrade notes

### General settings

These fields belong in **`settings.general.json` → `GeneralSettings.Application`**.

| Field | Default | Effective behavior |
|---|---|---|
| `StartupSellOnlyMode` | `true` | Native startup global SOM control, with the release and ownership rules above. Explicitly present in both supplied general-settings templates. |
| `ProfitTrailerServerAPITokenXtra` | `""` | Blank reuses the primary token. Otherwise comma-separated tokens correspond to additional PT URLs in the same order. |
| `MaxAnalysisAgeSeconds` | `300` | Permitted range 1–86400 seconds. Measures age from the raid pin at decision time. |
| `DecisionHistoryEnabled` | `true` | Enables bounded decision records. |
| `DecisionHistoryRetentionDays` | `30` | Permitted range 1–3650 days. |
| `DecisionHistoryMaxMegabytes` | `256` | Permitted range 16–102400; storage accounting uses MiB. |
| `TestMode` | `true` | Existing setting; 2.10 extends isolation to native SOM, acknowledgement checkpoints and SMS persistence. |

The supplied files are [`PTMagic/_defaults/_default_settings_PT_2.x/settings.general.json`][packaged-settings] and [`_Development/DevSettings/settings.general.json`][development-settings]. Both explicitly contain `"StartupSellOnlyMode": true` under `Application`. Existing installations obtain the default when the field is absent; a configured `false` remains an explicit opt-out subject to pending owned-operation recovery.

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
