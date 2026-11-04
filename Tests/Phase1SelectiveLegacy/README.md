# Selective legacy PriceTrend regression tests

Run `dotnet run --project Tests/Phase1SelectiveLegacy/Phase1SelectiveLegacy.csproj`.

Fourteen deterministic tests verify blank/omitted Metrics, explicit normalized
PriceTrend, unrelated metrics, explicit/implicit legacy GlobalSetting and
SMS/OffTrigger dependencies, volume/age/time-only triggers, non-Futures/CMC
compatibility, volume-ranked MaxMarkets, asset universe, allowed/ignored
markets and empty universe behavior.

The calculator runs only for trends selected by LegacyMarketTrendPlanner.
MarketTrendPopulationSelector independently selects normalized metric
populations from the frozen Phase 0 universe and current 24h volume ranks,
without requesting historical legacy price snapshots.

No live Binance or PT calls. Follow successful build and all regression
harnesses with TestMode raids that compare global population counts,
normalized trigger values, legacy SMS price triggers and 24h volume.
