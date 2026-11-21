# PTM 2.10 — Price-path efficiency test analyzer

**Identity:** `PTM2.10-PATH-EFFICIENCY-DIAGNOSTIC`  
**Version:** 1.0.0 — November 21, 2026  
**Application source:** [`49dd01f`](https://github.com/HojouFotytu/PTMagic/commit/49dd01f438f1c75f88a8873ff8e86bc1738f8d9b) or later  
**Status:** source fixture created and checked structurally; **configured LONG/SHORT TestMode execution has not yet been verified**.

This is a **new, standalone** analyzer fixture for validating the two new price-path measurements. It does **not** replace or modify the comprehensive [45-metric diagnostic analyzer](../settings.analyzer.json), and it is not a recommended market-regime configuration.

## File

[`settings.analyzer.json`](settings.analyzer.json) is ready to copy into a **separate, isolated** PTMagic TestMode instance as that instance's analyzer settings file. It refers to existing `DEFAULT` preset filenames (`PAIRS.properties`, `DCA.properties`, `INDICATORS.properties`); these must already exist with matching case in the target instance. No preset or strategy files are supplied or altered.

**Safety prerequisite:** Verify **`GeneralSettings.Application.TestMode=true`** in the instance's **separate** `settings.general.json`. The analyzer file alone cannot enable TestMode. Keep `StartupSellOnlyMode=true` for isolated testing and use no live instance. Do not copy the fixture to a production analyzer.

## What it exercises

| Configured example | Global measurement | Asset measurement | Context |
|---|---|---|---|
| `M30` (30 min) | `MedianPricePathEfficiency` | `PricePathEfficiency` | Existing `PriceTrend` / `MedianPriceTrend` |
| `H1` (60 min) | Same | Same | Existing price-direction measures |
| `H4` (240 min) | Same | Same | Existing price-direction measures |

All three MarketTrends use only the already-normalized **five-minute source candles**. `MaxMarkets=0` uses the full eligible global population; no hardcoded market allowlist, no PT-enabled-pair restriction and no per-asset cap are introduced. `StoreDataMaxHours=12` is a modest **test setting only** and is not a product-design constraint; any actual user-configured MarketTrend duration and its required retention remain independent.

The only extra triggers are **availability checks**:

- GlobalSetting `DIAG_G_PATH_EFFICIENCY_AVAILABLE` has an AND of three `MedianPricePathEfficiency` tests, one per MarketTrend. It matches only if all three global values are valid and within their mathematical 0–1 range.
- SMS `DIAG_SMS_PATH_EFFICIENCY_AVAILABLE` has an AND of three `PricePathEfficiency` tests for each individual market. It matches only for a market with three valid path measurements.
- The numeric bounds `[-0.000001, 1.000001]` provide tolerance around **0–1**, so exact zero and one both pass; **they are not regime thresholds**. A missing or invalid value remains UNAVAILABLE, not zero, and may prevent a match.

The default GlobalSetting preserves the existing three preset-file references. The diagnostic GlobalSetting and SMS have **empty** PAIRS/DCA/INDICATORS property dictionaries, do not introduce any setting overrides, and the SMS has no OffTriggers and `StopProcessWhenTriggered=false`.

## Acceptance checks

1. Confirm the source revision and run the existing solution build and focused suites (`Phase1COhlcv`, `Phase2AdaptiveAcquisition`, `ConfigurationLifecycle`) in the .NET-equipped development clone.
2. Use isolated `TestMode=true` LONG and SHORT instances with this **same** analyzer fixture; inspect the completed warm raid rather than assuming a cold first raid finishes inside the pinned decision-age guard.
3. On Market Analyzer, check the **Path Quality** global median values and per-asset `PricePathEfficiency` values across the three configured examples. Compare available price-direction context without treating efficiency as directional.
4. Verify that valid values stay in **[0,1]**, global snapshots retain the fixed eligible population and **at least 90% valid asset coverage**, and missing/flat/gapped/new-market paths are marked UNAVAILABLE rather than misreported as zero.
5. Review the per-raid data/decision history for both the global and per-market trigger resolution and source timestamp consistency. A missing metric must not activate its trigger. Preserve the exported ZIPs as validation evidence.

These checks establish **source and integration correctness only**. They do not measure trading results or predictive value. An analyzer configured to test value availability does not itself prove any particular regime definition.

**Not changed:** app code, the comprehensive diagnostic fixture, operating settings, exchange endpoints, raw history retention policy, PT or DEFAULT strategy files.
