# TESTING settings — all-metric probe

**Version:** 1.0.2  
**Date / timestamp:** 2026-11-11 / 2026-11-11T17:54:36+09:00  
**Branch:** `phase1/market-data-foundation`  
**Status:** Isolated diagnostic configuration; not a production LONG or SHORT analyzer.

## Purpose

This analyzer explicitly enables every metric in the PTMagic 2.10 metric registry inspected on `phase1/market-data-foundation` (source revision `de5ecc8c3b8ef81d1a6305beebd075185342e395`). It is intended to verify metric calculation, source availability, and per-asset trigger resolution. It is not a market-regime classifier or trading strategy.

The registry contains **29 unique metrics**: 16 Global-only, 12 Asset-only, and `PriceTrend` with Both scope. All 29 are enabled exactly once across four MarketTrends.

## MarketTrend coverage

| MarketTrend | Horizon | Metrics enabled |
|---|---:|---|
| `M30` | 30 minutes | `PriceTrend`, `MedianPriceTrend`, `MeanMedianSpread`, `Breadth`, `Dispersion`, `RelativeStrengthMarket`, `RelativeStrengthBTC`, `ATRP`, `VolumeExpansion`, `CorrelationToBTC` |
| `H1` | 1 hour | `Concentration`, `MedianATRP`, `VolatilityBreadth`, `MedianVolumeExpansion`, `VolumeExpansionBreadth`, `StrongVolumeExpansionBreadth`, `VolumeConcentration`, `TopNVolumeShare`, `BTCVolumeShare`, `BTCETHVolumeShare`, `MeanCorrelationToBTC`, `MedianCorrelationToBTC`, `OpenInterestChange`, `GlobalLongShortAccountRatio`, `TopTraderLongShortAccountRatio` |
| `H4` | 4 hours | `OpenInterestValueChange`, `TopTraderLongShortPositionRatio` |
| `H24` | 24 hours | `FundingRate`, `FundingSum` |

This deliberately uses four horizons rather than reproducing the production timeframe ladder. The longest normalized-OHLCV lookback is H1 correlation (30 returns plus the required close endpoint); `StoreDataMaxHours=72` caps normalized-OHLCV retention at 72 hours while providing sufficient history for the configured M30/H1 OHLCV metrics, including the H1 30-return correlation lookback. The H4/H24 derivatives feeds use their own source-specific history handling and do not require increasing this OHLCV retention. H4 and H24 are reserved for derivatives feeds and do not add longer normalized-OHLCV lookbacks.

## Diagnostic SMS

Four no-op SMS groups exercise every asset-scoped metric, plus the per-asset `PriceTrend`:

| Test SMS | Metrics |
|---|---|
| `TEST_SMS_01_ASSET_BASELINE` | `PriceTrend`, `RelativeStrengthMarket`, `RelativeStrengthBTC`, `ATRP`, `VolumeExpansion`, `CorrelationToBTC` |
| `TEST_SMS_02_OPEN_INTEREST` | `OpenInterestChange`, `OpenInterestValueChange` |
| `TEST_SMS_03_FUNDING` | `FundingRate`, `FundingSum` |
| `TEST_SMS_04_POSITIONING` | `GlobalLongShortAccountRatio`, `TopTraderLongShortAccountRatio`, `TopTraderLongShortPositionRatio` |

Each SMS uses an `AND` connection and deliberately broad numeric bounds. A group therefore activates only when all of its requested metrics resolve to valid values. These are neutral `TEST_SMS_` identities, not production `SLnn` or `SSnn` identities.

**The SMS property dictionaries are empty.** They do not override PAIRS, DCA, or INDICATORS properties. Their only purpose is to exercise per-market metric triggers and expose unavailable metrics. No SMS trading behavior is introduced.

## GlobalSettings

`GlobalSettings` contains only the required `DEFAULT` setting. It carries 16 broad-range OR triggers covering every Global-only metric; this makes the global trigger resolver evaluate those metrics without introducing another GlobalSetting. **No new GL settings are created.** The broad bounds are diagnostic pass-throughs, not regime thresholds. Asset-scoped metrics are additionally exercised through the diagnostic SMS groups.

## Derivatives data requirements

- **Exchange:** `BinanceFutures` is required for all seven new derivatives metrics.
- **Open interest:** `OpenInterestChange` and `OpenInterestValueChange` are separate percentage changes, using exact historical 5-minute endpoints at the configured MarketTrend horizon. The value metric uses the provider-reported OI value; it is not inferred from quantity and price.
- **Funding:** `FundingRate` is the latest regular settlement in the configured window; `FundingSum` is the sum of regular settlements in that window. Both are percentage points. The H24 window avoids assuming a fixed 8-hour settlement cadence. Funding history must be completely verified.
- **Long/short ratios:** all three are per-asset ratios calculated from a complete exact 5-minute series. Binance history is limited to 30 days.
- **API key:** `GlobalLongShortAccountRatio` does not require a key. `TopTraderLongShortAccountRatio` and `TopTraderLongShortPositionRatio` require the optional read-only `BinanceFuturesApiKey` in GeneralSettings. No API secret or signature is used. Without this key, those two metrics remain unavailable.

The per-raid collection budgets are set to 60 requests for each derivatives source family. With a large eligible market universe, asset-level coverage can be partial during an individual raid; inspect source coverage and unavailable reasons rather than interpreting missing values as zero.

## Safety and use

- `settings.general.example.json` sets `TestMode=true`. It is a local template: replace the PT credential and default-setting placeholders locally, and add the read-only Binance key if top-trader metrics are to be tested. Never commit credentials or keys.
- In TestMode, PT configuration delivery and remote SOM commands are simulated; the test configuration does not change the connected PT settings.
- The only GlobalSetting is the DEFAULT fallback. The SMS entries have no property overrides. No production GL, SL, or SS settings are changed.
- Run this as a separate PTMagic test configuration/instance. Do not replace either production LONG or SHORT analyzer configuration with this file.

## Source references

- [Canonical metric registry](https://github.com/HojouFotytu/PTMagic/blob/phase1/market-data-foundation/Core/MarketAnalyzer/MarketData/MarketTrendMetrics.cs)
- [Metric history requirements](https://github.com/HojouFotytu/PTMagic/blob/phase1/market-data-foundation/Core/MarketAnalyzer/MarketData/MarketTrendHistoryRequirements.cs)
- [PTMagic 2.10 changelog](https://github.com/HojouFotytu/PTMagic/blob/phase1/market-data-foundation/CHANGELOG-2.10.md)
