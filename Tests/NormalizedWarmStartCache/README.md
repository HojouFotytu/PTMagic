# Normalized OHLCV warm-start cache regression harness

The ten case groups exercise the production `MarketDataWarmStartCache` and `MarketDataService` using offline synthetic five-minute candles and temporary cache directories. No Binance/ProfitTrailer requests or live setting writes occur.

- Closed-only compressed snapshot round-trip, including exact OHLCV fields.
- Exchange and market identity isolation.
- Restart from a persisted window with only a three-candle incremental overlap request, versus a full cold-load request.
- A gap in a persisted series forces complete anchored backfill.
- An incomplete ordinary in-memory series still forces full backfill; only a successfully restored disk series can use the incremental shortcut.
- Truncated gzip is rejected without partially inserting its history.
- Restored observations obey a smaller current retention cap.
- Frequent unchanged saves are throttled.
- Realistic legacy v1 cache restoration, selective taker-volume repair, immediate version-2 persistence despite save throttling, and a subsequent restart without full-window backfill.
- A taker trend exceeding OHLCV capacity remains unavailable without forcing a full-history download on each raid.

Run from the PTMagic application repository root:

```powershell
dotnet run --project Tests/NormalizedWarmStartCache/NormalizedWarmStartCache.csproj
if ($LASTEXITCODE -ne 0) { throw "Normalized warm-start cache validation failed" }
```

Expected successful final line:

```text
Normalized warm-start cache tests passed: 10.
```

**Acceptance status:** Verified in user-local .NET SDK 10.0.403 at exact application source `925db7c153ee4d53cfe3b7bf736318e7a30bccf1` on November 9, 2026. `dotnet build .\PTMagic.sln` succeeded in 18.3 seconds (Core, PTMagic and Monitor). This harness passed 8/8. `Phase1AnalyticsReliability` passed 16/16 and `Phase1COhlcv` passed 13/13, totaling 37/37 focused checks. **Configured runtime acceptance still needs two startups:** cold cache creation, then a short restart with restored candle count, kline request count and unchanged analytics availability/values. The 272 previously passing regression groups were executed at earlier revisions, not as a full suite at `925db7c`.

Scope: persistent normalized five-minute OHLCV only. Legacy minute-tick reconstruction and first-seen-date acquisition are separate older data paths with their own disk/refresh rules; this cache does not change those paths.

**Phase 2 taker-flow update (2026-11-12):** Version-1 OHLCV snapshots remain readable, but do not assert taker volumes. Version 2 persists nullable, provider-verified taker-buy quote volumes. When requested by configured trends, only the required complete-period window is re-fetched and repaired; valid old OHLCV data remain in memory. Two taker-specific regression groups were added and **passed** at `f7d802facfabb4611098d695d88a269d60daa278`; the ten current groups passed 10/10.

**Focused verification — November 13, 2026 (KST):** User-run at PTMagic source [`f7d802facfabb4611098d695d88a269d60daa278`](https://github.com/HojouFotytu/PTMagic/commit/f7d802facfabb4611098d695d88a269d60daa278). `dotnet build .\PTMagic.sln` **PASS in 16.1 seconds** (Core, PTMagic, Monitor net10.0). `Phase1COhlcv` **15/15**, `NormalizedWarmStartCache` **10/10**, `Phase2AdaptiveAcquisition` **8/8** — **33/33 focused test groups PASS**. This establishes source-specific compilation and offline checks, **not** configured live Binance/TestMode metric values, PT settings delivery, or Monitor browser acceptance.
