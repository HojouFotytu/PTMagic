# Phase 1C OHLCV metrics deterministic tests

## November 25, 2026 — market legs, liquid breadth and ATR-normalized returns

The deterministic fixture exercises both the drawdown/recovery and rally/retracement directions. It checks a 50% price recovery, two returning participants per three original participants, complete versus zero leader overlap, a fresh extreme, an absent originating decline, coverage loss and a new excursion after a full recovery. The price path uses a common basket of equal initial investments throughout the rolling horizon. Each comparison uses the same starting, turning and ending closes. The strongest directional quarter on each leg includes ties at its cutoff.

The ranked-breadth fixture passes the exchange turnover order through the analytics entry point and verifies the independent coverage denominator for each rank. The normalized-return checks use a rolling return, or an asset’s excess over the broad median, divided by the same horizon’s positive ATR percentage. The market-wide ratio divides median return by median ATR percentage. The additional shock fixture divides current rolling median return by median ATR percentage at the beginning of the return window. It exercises unaligned UTC boundaries, wider current-candle ranges, a different historical volatility regime, a zero denominator, and gaps in the prior history. The 29 Phase1COhlcv and 21 Phase1DCorrelation offline test groups passed on the .NET 10 SDK.

## Current source/test boundary — November 19, 2026

The current `Tests/Phase1COhlcv/Program.cs` calls **22 focused test groups**, including the newer verified-no-trade/90%-coverage Phase 3 group and the taker failure-category diagnostics. The previously reported **20/20 PASS** belongs to the earlier `33a33f0` checkpoint; no newer **22/22 PASS** is established by this documentation update. Running or publishing a new test result requires separate evidence.

Current `4bbe299e` semantics for the four **Phase 3 global flow-price volume-share** metrics: at least **90%** of the frozen eligible market population observed, including source-verified no-trade assets as observed zero-volume periods. Other missing/invalid assets count against coverage and are excluded from numerator/denominator. The denominator consists of valid traded quote turnover and must be positive; a 90–99% observed subset can differ from the full cohort. The older 100% Phase 3 rule in the previous 20/20 test description is historical. Existing strict 100% quote-volume concentration requirements remain separate and unchanged.

The [trading-need-first assessment](../../CHANGELOG-2.10.md#november-19-2026--trading-need-first-metric-reassessment-documentation-only-design-authority) is analytical guidance, not an implemented extra metric or change to the test contract. Current `StoreDataMaxHours=96` diagnostic retention is not a permanent analytical limit; source history must follow justified metric needs.

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
