# P1A-READY — Cold-start cross-sectional completeness

**State:** OPEN / BLOCKS Phase 1C  
**First observed:** 2026-11-04  
**Target branch:** `phase1/market-data-foundation`  
**Code reference at observation:** `9e00a1f25068ed23c3d64b708e0cc85b5a7eaef4`

## Verified observations from diagnostic handoff

| Raid | Reported normalized refresh | 4h asset validity |
|---|---|---|
| 1 (cold start) | 756/756 fetch success, 654,696 candles | **259/1,434** |
| 2 | 557/756 series refreshed, 1,671 candles | **1,434/1,434** |
| 3 | 0 kline requests | Current; valid global metrics reported |

The cause is **not established**. Mixed common endpoints after a long bootstrap (around 9m30s with rate-limit cooldowns) is a hypothesis. Successful transport/fetch does not prove analytical readiness.

## Required investigation

- [ ] Inspect actual `MarketDataService.RefreshAsync`, `MarketDataStore`, Binance kline loader and normalized engine endpoint choice.
- [ ] Log latest closed timestamp and contiguous historical lookback per symbol, grouped against one explicit frozen expected endpoint.
- [ ] Explain exactly why first-raid validity was 259/1,434 and why the second raid fetched 557 symbols.
- [ ] Distinguish not-yet-ready from new/sparse/delisted data.
- [ ] Calculate eligible/expected/valid/missing/stale/invalid counts and explicit invalid reasons per trend and metric.
- [ ] Choose documented minimum coverage ratio and minimum valid sample policy, with denominator made visible.
- [ ] Make incomplete market-wide readings invalid and block associated normalized triggers; preserve valid independent features and legacy path.
- [ ] If verified necessary, catch up missing candles with bounded requests and existing Binance throttling, without forcing full-history downloads.
- [ ] Test startup crossing a 5m boundary, delayed/throttled downloads, sparse symbols, missing candles and normal fast startup.
- [ ] Re-run TestMode first/second/third raids; inspect logs, summaries and MarketTrendAnalytics snapshot.

## Constraints

Do not substitute missing returns as zero; do not silently use nearest candles; do not expand retention without consent; do not redirect omitted-Metric legacy triggers; do not conflate optional disk history persistence with this correctness bug.

## Exit criterion

Record a **verified cause**, precise fix/containment, commit SHA, independent tests, complete first-raid coverage/validity result and confirmation that no invalid market-wide trigger can activate.
