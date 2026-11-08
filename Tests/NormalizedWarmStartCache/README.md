# Normalized OHLCV warm-start cache regression harness

The eight case groups exercise the production `MarketDataWarmStartCache` and `MarketDataService` using offline synthetic five-minute candles and temporary cache directories. No Binance/ProfitTrailer requests or live setting writes occur.

- Closed-only compressed snapshot round-trip, including exact OHLCV fields.
- Exchange and market identity isolation.
- Restart from a persisted window with only a three-candle incremental overlap request, versus a full cold-load request.
- A gap in a persisted series forces complete anchored backfill.
- An incomplete ordinary in-memory series still forces full backfill; only a successfully restored disk series can use the incremental shortcut.
- Truncated gzip is rejected without partially inserting its history.
- Restored observations obey a smaller current retention cap.
- Frequent unchanged saves are throttled.

Run from the PTMagic application repository root:

```powershell
dotnet run --project Tests/NormalizedWarmStartCache/NormalizedWarmStartCache.csproj
if ($LASTEXITCODE -ne 0) { throw "Normalized warm-start cache validation failed" }
```

Expected successful final line:

```text
Normalized warm-start cache tests passed: 8.
```

**Acceptance status:** authored, not yet executed in the user's .NET environment. Runtime acceptance still needs two startups: cold cache creation, then a short restart with restored candle count, kline request count, and no changed analytics values.

Scope: persistent normalized five-minute OHLCV only. Legacy minute-tick reconstruction and first-seen-date acquisition are separate older data paths with their own disk/refresh rules; this cache does not change those paths.
