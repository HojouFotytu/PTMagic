# Raid metric-warning natural-language regression tests

## November 26–27 — historical Delta availability (Dev 17, source implemented, native execution pending)

`df260227` retains the previous Delta analysis and independently checks previous asset/benchmark observations. A verified continuous listing-origin series and the metric's actual historical requirement are necessary for an expected history classification. Separate current versus Delta classification avoids concealing actual current data failures. A valid current efficiency with a nonstale historical flat path is informational when all affected requests have matching verified evidence; mixed or genuine faults stay WARN. Values and three-valued trigger decisions remain UNAVAILABLE, not zero. The historical-flat-path regression is added; expand direct new-listing Delta and mixed current/historical benchmark/source-gap cases during acceptance. User's old-build SHORT raid #12 had duplicate Swing72 PricePathEfficiency and RelativeStrengthMarket Delta warnings solely for AMDLUSDT/JNJUSDT/NIOUSDT, already named in the INFO line; a valid historical median benchmark must be verified before classifying the latter as expected. No current-code warning harness execution or dual-instance replay has been reported. The earlier per-market-notice design below is historical; one consolidated market list per raid is current.


## November 23 — One-line new-market history reporting

Verified new-market history exclusions now produce exactly one INFO line per raid:
`+ New markets with insufficient history, excluded from some metrics: AMDLUSDT, JNJUSDT, NIOUSDT`.
No individual market notices, listing timestamps, affected-metric counts, or additional recovery announcements are printed in the summary. The existing classification, source verification, metric validity, and WARN treatment of genuine gaps remain untouched. The focused regression checks single- and six-market lists, repeat raids, and empty results. Build/runtime verification pending.

## November 23 — Move flat-path DEBUG out of raid summary

Flat `PricePathEfficiency` source DEBUG is now emitted alongside each MarketTrend's calculation. Related SMS caller details are emitted at the moment each unavailable trigger is evaluated, using the standard file DEBUG logger. `BuildFlatPathDebugLines` and its end-of-raid call are removed: only the concise INFO group remains in the summary. Verified NO_TRADES DEBUG also moves to metric evaluation time. The ten-caller regression checks all entries already exist before summary construction and that the summary does not replay them. No live progress-dot calls, metric values, trading settings or delivery logic were modified.

**Validation:** Source and focused tests authored; build and real runtime tests pending.


## November 23 — Flat-path price-efficiency INFO and complete DEBUG provenance (new tests pending)

Exact source-verified `PricePathEfficiency` 0/0 results from fully flat, nonstale closed-candle paths are expected mathematical unavailability and are no longer counted as Metric WARN groups. The raid INFO summary now uses one line per eligible trend/metric, e.g. `+ H1 PricePathEfficiency: 4/729 unavailable (flat price paths; 10 SMS trigger evaluations).`. A separate file DEBUG detail names **all** affected markets and **all distinct SMS caller identities** (setting/trigger/market/relation), not only the former five bounded WARN examples. The underlying unavailable metric value, three-valued trigger truth, per-market SMS protective WARN and configuration-delivery safeguards are unchanged.

The downgrade is deliberately narrow: missing/invalid/stale candles, mixed flat-path-plus-source faults, incomplete snapshots, unknown metrics and global-selection issues remain WARN. The exact flat source reason is shared as `PricePathEfficiencyEngine.FlatPathUnavailableReason` to avoid diverging string classifications. Existing NO_TRADES and verified-new-listing diagnostics are unaffected.

Two new focused regression groups check 4/729 flat cases, 10 complete DEBUG caller records, raid reset, mixed flat/stale faults and an explicitly stale observation carrying a flat-path reason. **Authored but not executed in this environment; owner-run solution build, Phase1MetricWarnings suite and deployed logs still require verification.**


## November 19 — Verified new-listing history diagnostics (source added; execution pending)

The warning pipeline now distinguishes exchange-verified **pre-listing historical shortages** from genuine missing or bad data. It uses native Binance Futures `exchangeInfo.onboardDate` and current contiguous 5m series beginning at the listing boundary, without changing metric availability or trade decisions. Only the particular metric/timeframe outputs whose required history predates listing become one INFO notice per market rather than repeated Metric Warnings. The raid still reports the number of affected new markets; unchanged detail is not repeated every raid.

The focused `Program.cs` suite currently contains **21 test groups** (19 existing calls, including verified NO_TRADES suppression, plus two new-listing diagnostics). The new groups exercise eight overlapping invalid metrics on one listed symbol, >5 SMS callers, preserved genuine invalid numeric warnings and GlobalSetting indeterminate decisions, missing listing metadata, post-listing source gaps, truncated caches, repeated notices and recovery. **21/21 execution has not been confirmed** here. Run the project command below on the intended source SHA, then check live TestMode LONG/SHORT logs independently. Previous historical 18/18 PASS claims and earlier source-specific totals are not proof that the new code compiles or runs.

Run locally:

```powershell
dotnet run --project Tests/Phase1MetricWarnings/Phase1MetricWarnings.csproj
```

Eighteen deterministic cases test normal output, reset, grouped warnings, global
coverage, unavailable asset metrics, unknown metrics, GlobalSetting hold behavior,
bounded caller examples, multiple sources, mixed failure causes, implicit
PriceTrend, and Option C natural-language explanations:

- Excluded or filtered markets, identifying SMS setting and recommended fix.
- Asset-only metrics incorrectly used by GlobalSettings, with possible alternatives.
- Explicit metrics not enabled on the referenced MarketTrend.
- Insufficient candle/volume history, with period counts.
- Invalid global coverage, explaining valid versus required market counts.
- Multiple affected activation/OffTrigger callers summarized in one warning.

Each warning group now produces one actionable sentence in the RAID SUMMARY.
The collector retains grouping and never changes metric calculations, trigger
truth values, or PT property updates.

Expected totals: **18/18** in this harness; **81/81** across all six
regression harnesses, pending local execution. Prior 75/75 is not evidence for
this change.

Under D-021, the user runs builds, regression tests and TestMode locally.
Check the new warning sentences on Binance Futures, Spot and Binance US, and
confirm existing settings/OffTrigger behavior remains unchanged.

## Mixed new-market warning groups (Dev 18)

Verified listing-related SMS failures are removed individually before warning rendering, preserving unrelated failures, recalculated counts, reason examples and caller identities (including those beyond the original display cap). Absolute and Delta use their respective classification. Original evidence remains intact. Native execution pending.
