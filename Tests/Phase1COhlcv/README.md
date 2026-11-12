# Phase 1C OHLCV metrics deterministic tests

Run: `dotnet run --project Tests/Phase1COhlcv/Phase1COhlcv.csproj`

Fixtures cover 10-minute trend aggregation from 5-minute source data,
Wilder ATR(14) seed and recurrence, close-only endpoint, quote-volume
expansion, invalid history/gaps/zero baseline/staleness/misalignment,
90% cross-section coverage, 100% concentration completeness, median and
breadth values, BTC/ETH shares, registry scopes and shared analytics
snapshot integration, plus exact complete-period taker-buy ratio/imbalance calculations, ratio-of-sums weighting, and unavailable legacy/zero-volume sources. The 24h ATRP insufficient-72h test exercises the
actual daily trend-bar builder.

No Binance calls are made. The tests must be run by a .NET 10 SDK
environment, then followed by Binance Futures TestMode validation.

**Focused verification — November 13, 2026 (KST):** User-run at PTMagic source [`f7d802facfabb4611098d695d88a269d60daa278`](https://github.com/HojouFotytu/PTMagic/commit/f7d802facfabb4611098d695d88a269d60daa278). `dotnet build .\PTMagic.sln` **PASS in 16.1 seconds** (Core, PTMagic, Monitor net10.0). `Phase1COhlcv` **15/15**, `NormalizedWarmStartCache` **10/10**, `Phase2AdaptiveAcquisition` **8/8** — **33/33 focused test groups PASS**. This establishes source-specific compilation and offline checks, **not** configured live Binance/TestMode metric values, PT settings delivery, or Monitor browser acceptance.

**Earlier evidence:** Historical 13/13 OHLCV success belongs to the pre-taker source.
