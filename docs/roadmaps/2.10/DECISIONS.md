# PTMagic 2.10 — Decision Register

Accepted decisions are not inferred from a merely proposed feature. Add a dated entry whenever an actual design decision is made.

| ID | Date | Decision | State |
|---|---|---|---|
| D-001 | 2026-11-04 | All Phase 0–6 market-analysis work is for **PTMagic 2.10**; 2.9 modernization is finished. | Accepted |
| D-002 | Phase 0 | Keep the legacy `MarketData_*.json` and legacy trend/settings pipeline intact; normalized 5m OHLCV is a parallel new layer. | Accepted |
| D-003 | 2026-11-04 | Each normalized metric uses the *actual* Phase 0-resolved per-trend universe; do not independently rebuild its own universe. | Accepted |
| D-004 | 2026-11-04 | Omitted `Trigger.Metric` uses legacy trigger behavior; optional normalized metrics are enabled by comma-separated `MarketTrend.Metrics`. | Accepted |
| D-005 | 2026-11-04 | Missing/invalid/stale normalized metrics fail closed, never substitute zero. | Accepted |
| D-006 | 2026-11-04 | `Concentration` remains Top10 **return advantage** (pp); volume share gets a separate metric name. | Accepted |
| D-007 | 2026-11-04 | Phase 4 = trigger integration, Phase 5 = liquidity, correcting earlier conflicting numbering. | Accepted |
| D-008 | 2026-11-04 | Cold-start completeness must be resolved or explicitly contained before new Phase 1C metrics. Disk warm-start cache is a separate optional performance project. | Accepted |
| D-009 | 2026-11-04 | Preserve historical roadmap revisions and handoff messages; edit current status/update log rather than overwriting source history. | Accepted |

## Open decisions requiring evidence before Phase 1C

- OD-001: Exact first-raid common endpoint and complete-series readiness / minimum coverage policy, including denominator and newly listed assets.
- OD-002: ATRP: TR formula, Wilder vs alternative smoothing, ATR seed/warm-up, period (roadmap suggests initial 14), 5m closed candles, denominator.
- OD-003: How ATR interval and lookback relate to `MarketTrend.TrendMinutes` without silently conflating measurement timeframes.
- OD-004: Exact metric names and registration/config schema for volatility breadth and volume families.
- OD-005: Quote-volume expansion window and previous comparable N closed intervals, zero baseline and minimum-history policy.

Do not treat open decisions as already configured in application code.
