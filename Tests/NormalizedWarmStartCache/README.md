# Normalized OHLCV warm-start cache regression harness

The nine case groups exercise the production `MarketDataWarmStartCache` and `MarketDataService` using offline synthetic five-minute candles and temporary cache directories. No Binance/ProfitTrailer requests or live setting writes occur.

- Closed-only compressed snapshot round-trip, including exact OHLCV fields.
- Exchange and market identity isolation.
- Restart from a persisted window with only a three-candle incremental overlap request, versus a full cold-load request.
- A gap in a persisted series forces complete anchored backfill.
- An incomplete ordinary in-memory series still forces full backfill; only a successfully restored disk series can use the incremental shortcut.
- Truncated gzip is rejected without partially inserting its history.
- Restored observations obey a smaller current retention cap.
- Frequent unchanged saves are throttled.
- Realistic legacy v1 cache restoration, selective taker-volume repair, immediate version-2 persistence despite save throttling, and a subsequent restart without full-window backfill.

Run from the PTMagic application repository root:

```powershell
dotnet run --project Tests/NormalizedWarmStartCache/NormalizedWarmStartCache.csproj
if ($LASTEXITCODE -ne 0) { throw "Normalized warm-start cache validation failed" }
```

Expected successful final line:

```text
Normalized warm-start cache tests passed: 9.
```

**Acceptance status:** Verified in user-local .NET SDK 10.0.403 at exact application source `925db7c153ee4d53cfe3b7bf736318e7a30bccf1` on November 9, 2026. `dotnet build .\PTMagic.sln` succeeded in 18.3 seconds (Core, PTMagic and Monitor). This harness passed 8/8. `Phase1AnalyticsReliability` passed 16/16 and `Phase1COhlcv` passed 13/13, totaling 37/37 focused checks. **Configured runtime acceptance still needs two startups:** cold cache creation, then a short restart with restored candle count, kline request count and unchanged analytics availability/values. The 272 previously passing regression groups were executed at earlier revisions, not as a full suite at `925db7c`.

Scope: persistent normalized five-minute OHLCV only. Legacy minute-tick reconstruction and first-seen-date acquisition are separate older data paths with their own disk/refresh rules; this cache does not change those paths.

**Phase 2 taker-flow update (2026-11-12):** Version-1 OHLCV snapshots remain readable, but do not assert taker volumes. Version 2 persists nullable, provider-verified taker-buy quote volumes. When requested by configured trends, only the required complete-period window is re-fetched and repaired; valid old OHLCV data remain in memory. A new offline group was authored but **has not yet been executed at the new source**.
