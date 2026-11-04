# PTMagic 2.10 — Current Development Status

**As-of:** 2026-11-04 initial tracking import. **Evidence:** the user-supplied revised roadmap and diagnostic summary. These labels are historical reported results; current HEAD and runtime must be checked again after any code change.

| Stable ID | Phase / deliverable | Status | Next action or evidence |
|---|---|---|---|
| P0-AUDIT | Phase 0 architecture audit | DONE | [Phase 0 audit](audits/phase-0-architecture-audit.txt) |
| P1A-STORE | Normalized 5m Binance Futures OHLCV store, refresh and universe integration | DONE (reported) | Reverify interfaces before extending |
| P1A-READY | Cold-start cross-sectional completeness / readiness | **ISSUE — BLOCKING** | [Investigation](issues/001-cold-start-readiness.md) |
| P1B-RET | Normalized PriceTrend / MedianPriceTrend / MeanMedianSpread | DONE (reported) | Keep independent arithmetic tests |
| P1B-BREADTH | Positive-return Breadth and Dispersion | DONE (reported) | Coverage gate is separate from arithmetic |
| P1B-RS | RelativeStrengthMarket / RelativeStrengthBTC | DONE (reported) | Scope and pp units preserved |
| P1B-CONC | Top10 *return* Concentration | DONE (reported) | Do not confuse with volume share |
| P1C-01 | Per-asset ATRP | NEXT after P1A-READY | Finalize TR/ATR definition, interval, length, smoothing, denominator |
| P1C-02 | Market median ATRP | NEXT after P1C-01 | Same resolved Phase 0 universe, explicit coverage |
| P1C-03 | Volatility breadth | PLANNED | Specify ATRP threshold/denominator/config |
| P1C-04 | Quote-volume expansion | PLANNED | Closed windows, comparable prior periods, zero baseline |
| P1C-05 | Volume-expansion breadth | PLANNED | Valid/eligible counts and thresholds |
| P1C-06 | Volume concentration | PLANNED | Volume shares; independent of existing Concentration |
| P1C-07 | Metric registration, Monitor export and testing | PLANNED | Extend existing path, no parallel architecture |
| P1D-CORR | Synchronized BTC correlation / market cohesion | PLANNED | First generation O(n), evaluate pairwise later |
| P1E-INSPECT | UI, independent validation, negative TestMode checks | PARTIAL | Continue as metrics are added |
| P2-RAW | Futures OI, funding, positioning, taker flow | PLANNED | Verify API scope/availability before implementation |
| P2B-AGG | Futures cross-sectional aggregation | PLANNED | Requires P2 |
| P3-STATE | Deterministic higher-order futures states | PLANNED | Transparent component measures |
| P4-TRIG | GlobalSetting/SMS/OffTrigger metric routing | PARTIAL | First-generation resolver reportedly works; extend per metric |
| P5-LIQ | Bid/ask liquidity, depth and market stress | PLANNED | Later expensive endpoint family |
| P6-HIST | Time-series persistence and validation | PLANNED | Later, separate from cold-start correctness |

## Current blocking investigation — P1A-READY

- First raid: 756/756 normalized series reported successfully fetched, 654,696 candles, 717 eligible symbols, **259/1,434 valid 4h asset metrics**.
- Second raid: 557/756 series refreshed and **1,434/1,434** valid.
- Third raid: zero kline requests; approximately 1-second raid runtime reported.
- Cause is **unknown**. Time boundary crossing / mixed endpoints is only a hypothesis.
- **Do not** repair by substituting zero, weakening exact endpoint alignment, assuming initial success means readiness or silently widening retention.
- Required result: clear common-endpoint readiness diagnosis, explicit cross-sectional coverage gates, fail-closed dependent normalized triggers, TestMode verification.

## Exact metric names in implemented set

Global: `PriceTrend`, `MedianPriceTrend`, `MeanMedianSpread`, `Breadth`, `Dispersion`, `Concentration`.

Asset: `RelativeStrengthMarket`, `RelativeStrengthBTC`.

The current `Concentration` means Top10 return advantage in percentage points, not volume share.

## Version distinction

2.9 = completed modernization release. **2.10** = active Phase 0–6 market-analysis development. The historical diagnostic binary identified itself as 2.9.1; do not relabel those logs.

## Next review

After inspecting current code and the recorded diagnostic context: document the verified initial-raid failure mechanism, the smallest safe fix, the test plan, and its actual commit/test evidence. Only then change P1A-READY to DONE or explicitly CONTAINED.
