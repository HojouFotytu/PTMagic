# Phase 1C OHLCV metrics deterministic tests

## Current Phase 3 / taker diagnostics status — November 17, 2026

The user ran `Phase1COhlcv` **20/20 PASS** after `33a33f0` corrected an H72 epoch-aligned *test fixture* for the Phase 3 implementation `cbe919aa`. The eight Phase 3 metrics were tested for all four confirmation/divergence conditions, quote-volume shares, neutral/invalid values, 100% global coverage, the excluded partial UTC bar and H72 duration.

**New source `f45360d` has a 21st focused test group, UNEXECUTED.** `TestTakerFailureReasonCategories` distinguishes missing or misaligned candles, a missing/unverified taker field, complete zero-total-quote-volume (undefined 0/0), positive totals containing an individual zero-volume bar, and defensive invalid numeric taker data. Existing old-form reason assertions were also sharpened. No new user-run `21/21` result exists. Run `dotnet run --project .\Tests\Phase1COhlcv`. The LONG/SHORT TestMode logs uploaded at revision `24d70402` predate these new diagnostic strings; zero volume for every affected market is not yet independently established.




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
