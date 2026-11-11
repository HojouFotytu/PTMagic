# PTMagic 2.10 — latest-build diagnostic analyzer

**Source branch:** `phase1/market-data-foundation`  
**Pinned source revision:** `5a21a482711c8026b50fce10c55b3fb76f4075c3`  
**Test version:** 1.0.0  
**SetDate:** 2026-11-11  
**SetTimestamp:** 2026-11-11T23:52:51+09:00  
**Scope:** Isolated TEST-only configuration. This is separate from the older `Tests/Settings/settings.analyzer.json`; neither production settings nor application code are changed.

## Purpose

Verify **all 27 metrics in the currently inspected PTMagic 2.10 metric registry**, with valid global-versus-asset trigger scopes. The previous 29-metric test is outdated: `TopTraderLongShortAccountRatio` and `TopTraderLongShortPositionRatio` were removed and are **not configured here**. The old per-raid knobs `OpenInterestMaxRequestsPerRaid`, `FundingMaxRequestsPerRaid`, and `LongShortRatioMaxRequestsPerRaid` were also removed; the latest build uses adaptive acquisition under the shared Binance limiter.

## MarketTrends and coverage

| MarketTrend | Minutes | Metric count | Enabled metrics |
| --- | ---: | ---: | --- |
| M30 | 30 | 10 | PriceTrend, MedianPriceTrend, MeanMedianSpread, Breadth, Dispersion, RelativeStrengthMarket, RelativeStrengthBTC, ATRP, VolumeExpansion, CorrelationToBTC |
| H1 | 60 | 14 | Concentration, MedianATRP, VolatilityBreadth, MedianVolumeExpansion, VolumeExpansionBreadth, StrongVolumeExpansionBreadth, VolumeConcentration, TopNVolumeShare, BTCVolumeShare, BTCETHVolumeShare, MeanCorrelationToBTC, MedianCorrelationToBTC, OpenInterestChange, GlobalLongShortAccountRatio |
| H4 | 240 | 1 | OpenInterestValueChange |
| H24 | 1440 | 2 | FundingRate, FundingSum |

Every metric is enabled exactly once. There are **17 globally resolvable metrics** (16 global-only and PriceTrend), each included in the broad-range `DEFAULT` GlobalSetting trigger set. The per-asset metrics are evaluated with **four diagnostic SMS**, totaling 11 triggers:

- `TEST_SMS_01_BASELINE`: PriceTrend, RelativeStrengthMarket, RelativeStrengthBTC, ATRP, VolumeExpansion, CorrelationToBTC
- `TEST_SMS_02_OPEN_INTEREST`: OpenInterestChange, OpenInterestValueChange
- `TEST_SMS_03_FUNDING`: FundingRate, FundingSum
- `TEST_SMS_04_POSITIONING`: GlobalLongShortAccountRatio

SMS groups have empty PAIRS/DCA/INDICATORS property maps, no off triggers, and broad `AND` comparisons. Global DEFAULT uses `OR` so some unavailable metrics do not necessarily prevent selecting the fallback. Trigger rows are resolved individually, including unavailable values. The broad bounds are probes, not strategy thresholds.

## Population and retention

- `StoreDataMaxHours=72`; `IntervalMinutes=5`.
- `IncludedUnderlyingTypes=["COIN"]`; `UseProfitTrailerAllowedUnderlyingTypes=false`. This is an isolated testing eligibility policy, not the user's production strategy policy.
- All four MarketTrends and all four SMS groups use the same asset allowlist: `BTC,ETH,BNB,SOL,XRP,DOGE,ADA,LINK,LTC,AVAX,BCH,TRX,DOT,ATOM,XLM,NEAR`. These are **candidate** USD-M market symbols and remain subject to live Binance eligibility and availability.
- `MaxMarkets=0` avoids a separate global aggregation cap. `TopNVolumeCount=5`. The separate `Concentration` metric requires a **complete Top10 plus at least one remaining market**, so at least 11 allowed markets must actually be eligible and have complete H1 OHLCV data.
- M30 and H1 OHLCV-dependent lookbacks are within 72-hour capacity (including 30-period BTC correlations), although actual source completeness remains independently required.
- H4 OI and H24 funding use source-specific histories independent of the normalized OHLCV `StoreDataMaxHours` value.
- The allowlist governs MarketTrend and SMS evaluation populations, but does **not guarantee** that every upstream kline fetch is limited to those same markets.

## Using the test

1. Use `settings.analyzer.json` in an **isolated PTMagic 2.10 TestMode instance** at the source revision above. This folder is not automatically loaded by PTMagic.
2. Adapt `settings.general.example.json` locally, including the existing PT license, API token, Monitor URL and **correct existing PT preset name**. Never commit secrets or placeholder substitutions. The template has `TestMode=true`.
3. Retain an existing compatible PAIRS/DCA/INDICATORS preset trio. The tester introduces no new strategy preset and makes no production trading overrides.
4. Review the Market Metrics page, `MarketTrendAnalytics.json`, decision history, and raid summary. Check `AssetSymbols` and `GlobalSymbols` in the actual outputs before treating any unavailable data as a calculation failure.
5. Inspect adaptive collection progress over subsequent raids: exact OI H1/H4 endpoints, complete H24 regular funding history, and full H1 five-minute global long/short ratio history. Missing or unpolled source data remain UNAVAILABLE and must not be interpreted as zero.
6. Observe warnings for stale/incomplete metrics and distinguish population exclusions from exchange-history gaps. This is **runtime testing**, not evidence of a trading advantage.

The only supported positioning metric in this source revision, `GlobalLongShortAccountRatio`, uses Binance's public endpoint and **does not require an additional API key**.

**Not yet verified:** local PTMagic build, actual configured raid execution, live Binance source completeness, Monitor browser output, or PT order behavior. GitHub configuration-file presence is not runtime acceptance.

## Sources

- [Pinned registry](https://github.com/HojouFotytu/PTMagic/blob/5a21a482711c8026b50fce10c55b3fb76f4075c3/Core/MarketAnalyzer/MarketData/MarketTrendMetrics.cs)
- [Pinned history requirements](https://github.com/HojouFotytu/PTMagic/blob/5a21a482711c8026b50fce10c55b3fb76f4075c3/Core/MarketAnalyzer/MarketData/MarketTrendHistoryRequirements.cs)
- [Adaptive acquisition test](https://github.com/HojouFotytu/PTMagic/blob/5a21a482711c8026b50fce10c55b3fb76f4075c3/Tests/Phase2AdaptiveAcquisition/README.md)
