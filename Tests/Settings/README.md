# PTMagic 2.10 — retained-metrics diagnostic analyzer

**Updated:** November 12, 2026 · **Scope:** D-053, 24 canonical metrics.
**Branch:** `phase1/market-data-foundation` · **Test-only**, no production strategy or PT preset changes.

**Build target:** [`e31635a1e67347918ca1bbf94d0fb64809b1cea0`](https://github.com/HojouFotytu/PTMagic/commit/e31635a1e67347918ca1bbf94d0fb64809b1cea0) · **Diagnostic configuration:** `2.0.1` (updated in place). User-reported `dotnet build .\PTMagic.sln` **PASS** (12.9s); `Phase2Funding` **28/28 PASS**; `Phase2AdaptiveAcquisition` **8/8 PASS**. This verifies source compilation and focused offline checks, **not** configured PTMagic raids, live Binance/PT behavior or Monitor browser acceptance. No new metrics or test directories were added; the 24-metric, three-trend analyzer and its trigger thresholds are unchanged.

## Metric population

| Trend | Minutes | Count | Metrics |
|---|---:|---:|---|
| M30 | 30 | 10 | PriceTrend, MedianPriceTrend, MeanMedianSpread, Breadth, Dispersion, RelativeStrengthMarket, RelativeStrengthBTC, ATRP, VolumeExpansion, CorrelationToBTC |
| H1 | 60 | 12 | Concentration, MedianATRP, VolatilityBreadth, MedianVolumeExpansion, VolumeExpansionBreadth, StrongVolumeExpansionBreadth, VolumeConcentration, TopNVolumeShare, BTCVolumeShare, BTCETHVolumeShare, MeanCorrelationToBTC, MedianCorrelationToBTC |
| H24 | 1440 | 2 | FundingRate, FundingSum |

Every retained canonical metric is selected once. The DEFAULT GlobalSetting uses 17 global-capable trigger metrics; `TEST_SMS_01_BASELINE` probes six normalized asset metrics, and `TEST_SMS_02_FUNDING` probes FundingRate and FundingSum. All comparisons are diagnostic, not trading thresholds.

The analyzer filters `IncludedUnderlyingTypes=["COIN"]`, `UseProfitTrailerAllowedUnderlyingTypes=false`, and has `MaxMarkets=0` on all trends. No 16-symbol allowlist is imposed; a prior README incorrectly claimed otherwise. The actual eligible COIN population is resolved using Binance metadata and current USDT markets before historical acquisition and SMS evaluation. Funding has its own exact source coverage and is independent of OHLCV retention hours.

Use the settings in an isolated PTMagic **TestMode** installation with separately supplied local credentials and an existing valid preset trio. The committed `settings.general.example.json` contains placeholders only and is not automatically loaded. Observe raid summaries, three trends, two SMS groups, metric colors/availability and funding bulk acquisition. Keep historical `_data` cache directories for audit; the removed services no longer open them. Offline tests do not establish configured Binance/PT/Monitor acceptance.
