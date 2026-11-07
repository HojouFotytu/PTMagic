# Phase 1D correlation harness

21 deterministic case groups cover Pearson coefficients (+1, -1, 0, 0.4),
constant variance, timestamp/count/end-boundary rejection, simple-return vectors,
full source continuity, the N+1 reference close, partial-period exclusion,
all supported example trend durations, stale data, lookback bounds, 90% global
coverage, exact independent populations including explicitly selected BTC,
missing BTC, aligned Fiat conversion, registry units, and shared-engine merging.

This is source-authored coverage. It has **not been executed** in this work;
build and test execution remains user-owned under D-021.

```sh
dotnet run --project Tests/Phase1DCorrelation/Phase1DCorrelation.csproj
```

All candles and clocks are in memory. The harness makes no exchange/API requests.
