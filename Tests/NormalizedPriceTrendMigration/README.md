# Normalized PriceTrend compatibility regression

Run locally from repository root:

```powershell
git pull --ff-only
dotnet build
dotnet run --project Tests/NormalizedPriceTrendMigration/NormalizedPriceTrendMigration.csproj
```

**18 in-memory cases**, no Binance HTTP and no ProfitTrailer writes:

- Blank and explicitly enabled normalized PriceTrend; explicit non-price-only metrics
- Omitted GlobalSetting/SMS Metric dependency resolution
- Exact closed endpoints, missing history and 90% readiness
- Separate MaxMarkets-capped global and uncapped asset populations
- Uncapped SMS Relative benchmark (activation and OffTrigger both use the same resolver), including PriceTrend-only TrendThreshold filtering
- PriceTrend outlier gate does not change median, breadth, dispersion or mean/median spread or the eligible market population
- Aligned quote-to-USDT Fiat conversion, USDT/USD identity policy, and fail-closed missing conversion
- TrendMinutes must be a positive multiple of five
- USD quote benchmark, metric scopes and all three Binance exchanges

The previous 12-case version did not cover uncapped SMS Relative, Fiat,
TrendThreshold isolation or timeframe divisibility. The full six-harness suite is
expected to run **88 cases** (Phase1AReadiness=5, Phase1COhlcv=13,
Phase1SmsSummaryIsolation=5, Phase1TriggerLogic=29,
NormalizedPriceTrendMigration=18, Phase1MetricWarnings=18).

All verification remains user-owned under D-021. Build/test success and live
TestMode are **not** claimed until user supplies output. Binance Futures, Spot,
and Binance US live Fiat and SMS OffTrigger behavior remain separate checks.
