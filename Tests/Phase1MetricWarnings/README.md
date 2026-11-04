# Raid metric-warning natural-language regression tests

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
