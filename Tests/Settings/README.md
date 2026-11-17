# PTMagic 2.10 — 43-metric multi-trend diagnostic analyzer

## Current LONG/SHORT configured TestMode evidence — November 17, 2026

**One shared direction-neutral diagnostic analyzer** can be used unchanged by **separately isolated** LONG and SHORT `TestMode=true` bots. Log ZIPs `PTMagic-Logs-2026-11-17T08-04-32Z.zip` and `08-04-36Z.zip` ran deployed application source `24d704026ac017082eab663daad6639d818c9021`, NOT current HEAD. **Each bot: 7 completed raids, first uncached raid 5m21s/deferred at pinned 300s, 6/6 later conclusive TestMode decisions**, no PT writes or PTMagic ERROR/CRITICAL. H1 four new global Phase 3 shares were **719/719 available (100%)** and held the same values within the complete UTC hour. `DIAG_SMS12_H1_PHASE3_FLOW` activated **682** markets in post-cold raids 2–6 and **692** in raid 7.

H1 global share changes across 17:00 KST (percent, not market breadth): buyer confirmation **0.9682541 → 3.84586799**, seller confirmation **4.44556117 → 0.99484792**, buyer divergence **0.90183547 → 0.08800639**, seller divergence **0.15749786 → 1.9748316**. The 100%-coverage policy remains strict.

**Open acceptance:** No *fresh decisive* `DIAG_G08_PHASE3_FLOW` GlobalSetting activation (earlier GS matched first); a temporary isolated TestMode G08-first diagnostic would address that separately. M15/M30 `TakerBuyRatio` and `TakerImbalance` had 2–4 grouped warnings, old text cannot distinguish zero volume from invalid accumulated values. `f45360d` added precise diagnostics but its OHLCV **21-group suite is pending**; `303b2e7` single-line SMS logging is **84/84 focused PASS** but these old deployed logs cannot verify the newer output. See the [Dev 10 → Dev 11 handoff](https://github.com/HojouFotytu/PTM_2.10_Roadmaps_Handoffs/blob/main/handoffs/2026-11-17-ptm-210-dev10-to-dev11.md).




**Updated:** 2026-11-17. **Fixture:** `settings.analyzer.json` v2.3.0. **Application:** PTMagic `cbe919aa` or later with Phase 3 (the H72 fixture correction is `33a33f0`). **Test-only:** run exclusively in an isolated Binance Futures instance with `TestMode=true` in a separate local `settings.general.json`.

## Exact coverage

- **10 MarketTrends:** M15, M30, H1, H2, H4, H6, H12, H24, H48 and H72, with **both global- and asset-capable metrics in every trend**, graph and market-list display enabled.
- **All 43 registered metrics selected** across timeframes: 30 global-capable and 13 asset-only; `PriceTrend` also has asset scope (14 asset-capable total). Every globally capable metric appears in a GlobalSetting trigger; every asset-capable metric appears in an SMS activation or OffTrigger. All eight new Phase 3 metrics are selected **only at H1**, already using taker-flow source candles.
- **9 GlobalSettings:** existing 7 non-default groups remain unchanged; `DIAG_G08_PHASE3_FLOW` adds four H1 global triggers with OR before final `DEFAULT`. The new group matches a positive quote-volume share (>0.000000001%; maximum bound 101%); the earlier settings keep their priority. All 8 non-default groups have **empty** property dictionaries and inherit the same DEFAULT preset.
- **12 SMS:** original 11 remain unchanged; `DIAG_SMS12_H1_PHASE3_FLOW` adds four H1 asset-only binary flag triggers with OR (match valid 1 via range 0.5–1.01). The new SMS has empty property dictionaries, does not stop processing, and has no OffTriggers.
- **96h of normalized source retention**, adjusted ATR/volume/correlation lookbacks on longer horizons, and H24/H48/H72 funding. `MaxMarkets=0` at every trend (no global cap). COIN, EQUITY and INDEX are eligible without PT allowed-pair filtering or a fixed allowlist.

| Trend | Minutes | Selected metrics | ATR period | Volume lookback | Correlation lookback |
|---|---:|---:|---:|---:|---:|
| M15 | 15 | 13 | 14 | 20 | 24 |
| M30 | 30 | 18 | 14 | 20 | 24 |
| H1 | 60 | 36 | 14 | 20 | 24 |
| H2 | 120 | 21 | 14 | 20 | 20 |
| H4 | 240 | 28 | 14 | 20 | 16 |
| H6 | 360 | 28 | 10 | 10 | 10 |
| H12 | 720 | 20 | 6 | 5 | 6 |
| H24 | 1440 | 20 | 14 | 20 | 30 |
| H48 | 2880 | 18 | 14 | 20 | 30 |
| H72 | 4320 | 12 | 14 | 20 | 30 |

The Market Analyzer's grouped **Market-Wide Metrics** and its **expandable per-asset rows** can therefore show the current catalogue on several horizons. H1/H4 provide the full non-funding metric range; H24/H48 include funding and taker groups. Values are shown **only if actually calculated and published** by the analysis engine; Monitor visibility does not activate new calculations. Published PriceTrend can also appear in expandable rows under the latest Monitor behavior. Missing source or insufficient coverage may legitimately show `--`; a selected metric does not guarantee an available value.

## Phase 3 metric and runtime contract

- **H1-only opt-in.** Phase 3 captures first-candle OPEN, final-candle CLOSE and total quote turnover during the existing H1 taker-ratio candle traversal. Existing rolling `PriceTrend` is not recalculated. No new API endpoint, extra candle pass, or expanded history window; the full H72 Phase 3 fixture is covered separately in the 20/20 offline OHLCV tests.
- **Asset metrics:** `BuyerFlowPriceConfirmation`, `SellerFlowPriceConfirmation`, `BuyerFlowPriceDivergence`, `SellerFlowPriceDivergence` return binary 0/1, and valid 0 is a nonmatch rather than missing data. **Global metrics:** `BuyerFlowPriceConfirmationVolumeShare`, `SellerFlowPriceConfirmationVolumeShare`, `BuyerFlowPriceDivergenceVolumeShare`, `SellerFlowPriceDivergenceVolumeShare` express matching absolute net aggressive quote flow divided by all eligible quote turnover, multiplied by 100 (percentage of native quote turnover). They are not market breadth or predictive price scores.
- **Availability:** all global Phase 3 volume shares require **100%** source-valid coverage of the fixed eligible market cohort. Unverified, missing or stale candles remain UNAVAILABLE. Because the global cohort is uncapped, one missing asset can invalidate a share. New G08's unknown global triggers can make a GlobalSetting decision indeterminate if earlier groups did not match; this is correct fail-closed behavior. Do not weaken the validity rule or assume such a raid proves a feature calculation fault.
- **Diagnostics:** new G08 and SMS12 are property-free; `DEFAULT` remains the final GlobalSetting, with original case-sensitive preset file names. Existing trigger priority and all original SMS/GS conditions are untouched. A new diagnostic need not be selected to have its metric calculated and exported. All operations remain in TestMode with no PT writes.

## Preset validity and diagnostic safety

The configured test instance uses **`_presets/DEFAULT`** and the default GlobalSetting is named **`DEFAULT`**, matching its case-sensitive Linux directory. The repository's example template is separately named `PTMagic/_presets/Default`; it is **not** the installed preset directory targeted by this test analyzer. It references `PAIRS.properties`, `DCA.properties` and `INDICATORS.properties`; verify the **installed local** file names match exact case. The other eight GlobalSettings contain **no `File` entries**, so PTMagic inherits the default trio rather than looking for missing diagnostic preset folders. **No preset directory or property file is created or renamed.**

Trigger thresholds are diagnostic examples, not investment/trading rules. Ordered global triggers may stop after the first match; other groups can be configured but not selected in a particular raid. SMS may similarly remain inactive because of real market conditions or missing data. OffTriggers exercise existing SMS lifecycle behavior. All GS/SMS property overrides are empty except the inherited DEFAULT file references. Do not load into a live trading installation.

## Verification boundary

The v2.3.0 fixture has been statically checked for all 43 registered metrics, scope and trend references, unique trigger tags, sensible diagnostic thresholds, preserved original groups, five-minute alignment, source-retention capacity, inherited preset references, and property-free diagnostics. The owner ran `dotnet build .\\PTMagic.sln` PASS (16.9s) at application source `cbe919aa`; after the test-only H72 alignment correction `33a33f0`, `Phase1COhlcv` PASS 20/20 and `Phase2AdaptiveAcquisition` PASS 9/9. **These precede this fixture change**; configured LONG and SHORT TestMode raids are still UNVERIFIED. Collect the running source SHA, H1 asset/global Phase 3 values in `MarketTrendAnalytics.json`, `LastRuntimeSummary.json`, `DecisionHistory`, full logs, 100% eligible coverage, data timestamps and repeat-raid timings. Do not infer real PT writes or browser acceptance.

Suggested focused commands from the application clone: `dotnet build .\PTMagic.sln`, `dotnet run --project .\Tests\Phase2Funding`, `dotnet run --project .\Tests\Phase1COhlcv`, `dotnet run --project .\Tests\Phase2AdaptiveAcquisition`, `dotnet run --project .\Tests\ConfigurationLifecycle`. Historic 26/35-metric fixture evidence does not validate the new 43-metric configured raid.

Only this existing diagnostic fixture and this README are updated. No application code, credentials, general settings, personal LONG/SHORT strategies, presets or directories are modified.
