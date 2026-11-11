# Phase 2 — Binance Futures Global Account Long/Short Ratio Checks

Focused offline checks for the single opt-in public Binance USD-M Futures
global-account long/short ratio feed. No API key or API secret is required. As of D-051.1, the ratio request
budget is no longer user-configurable. Production uses shared Binance
rate-limit and freshness-aware admission; bounded overloads remain for tests.
Legacy budget keys are ignored; explicit metric selection controls acquisition.

Run from the PTMagic repository root:

```powershell
dotnet run --project .\Tests\Phase2LongShortRatios\Phase2LongShortRatios.csproj
```

No live Binance calls, PT instance, or trading actions are required.

## Binance five-minute startTime boundary (November 12, 2026)

Live `BTCUSDT` public endpoint evidence: a request beginning one millisecond
after a five-minute candle boundary returned the observation at that exact
boundary plus the 12 in-window observations (13 provider rows). Service-level
validation now drops **only** the expected `startTime - 1 ms` aligned overlap.
Other out-of-window rows still fail closed. Provider `returned.Count`, rather
than the trimmed observation count, continues to determine whether a 500-row
page is exhausted. Focused tests cover complete 60-minute windows, persistent
cache exclusion of the extra sample, rejection of unrelated old rows, and
full-page pagination/restart behavior.

The fix does not modify endpoint request weights, capacity scheduling,
MarketTrend values, user configuration, or trading settings.
