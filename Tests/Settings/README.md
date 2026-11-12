# PTMagic 2.10 — retained-metrics diagnostic analyzer

**Updated:** November 12, 2026 · **Scope:** Phase 2, 26 canonical metrics.
**Branch:** `phase1/market-data-foundation` · **Test-only**, no production strategy or PT preset changes.

**Build target:** current Phase 2 taker-flow branch (source build and focused checks **pending**). **Diagnostic configuration:** `2.1.0` (updated in place). The prior `e31635a1` D-053 build and 36 focused checks passed before the taker change. All earlier 24 retained metrics and trigger thresholds remain unchanged; the two additional asset-only taker metrics are opt-in.

## Metric population

| Trend | Minutes | Count | Metrics |
|---|---:|---:|---|
| M30 | 30 | 12 | PriceTrend, MedianPriceTrend, MeanMedianSpread, Breadth, Dispersion, RelativeStrengthMarket, RelativeStrengthBTC, ATRP, VolumeExpansion, CorrelationToBTC, TakerBuyRatio, TakerImbalance |
| H1 | 60 | 12 | Concentration, MedianATRP, VolatilityBreadth, MedianVolumeExpansion, VolumeExpansionBreadth, StrongVolumeExpansionBreadth, VolumeConcentration, TopNVolumeShare, BTCVolumeShare, BTCETHVolumeShare, MeanCorrelationToBTC, MedianCorrelationToBTC |
| H24 | 1440 | 2 | FundingRate, FundingSum |

All 26 canonical metrics are selected once. The DEFAULT GlobalSetting uses 17 global-capable trigger metrics; `TEST_SMS_01_BASELINE` probes eight normalized asset metrics, and `TEST_SMS_02_FUNDING` probes FundingRate and FundingSum. All comparisons are diagnostic, not trading thresholds.

The analyzer filters `IncludedUnderlyingTypes=["COIN"]`, `UseProfitTrailerAllowedUnderlyingTypes=false`, and has `MaxMarkets=0` on all trends. No 16-symbol allowlist is imposed; a prior README incorrectly claimed otherwise. The actual eligible COIN population is resolved using Binance metadata and current USDT markets before historical acquisition and SMS evaluation. Funding has its own exact source coverage and is independent of OHLCV retention hours.

Use the settings in an isolated PTMagic **TestMode** installation with separately supplied local credentials and an existing valid preset trio. The committed `settings.general.example.json` contains placeholders only and is not automatically loaded. Observe raid summaries, three trends, two SMS groups, metric colors/availability and funding bulk acquisition. Keep historical `_data` cache directories for audit; the removed services no longer open them. Offline tests do not establish configured Binance/PT/Monitor acceptance.
