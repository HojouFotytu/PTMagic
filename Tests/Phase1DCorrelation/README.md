# Phase 1D correlation harness

21 deterministic case groups cover Pearson coefficients (+1, -1, 0, 0.4),
constant variance, timestamp/count/end-boundary rejection, simple-return vectors,
full source continuity, the N+1 reference close, partial-period exclusion,
all supported example trend durations, stale data, lookback bounds, 90% global
coverage, exact independent populations including explicitly selected BTC,
missing BTC, aligned Fiat conversion, registry units, and shared-engine merging.

**All 21 case groups passed** in the user's .NET 10.0.403 run at source
`86c2ea3ff430b62e654f929a222935ec53ea8fa6`. Build and test execution remains
user-owned under D-021; configured exchange/runtime acceptance remains separate.

```sh
dotnet run --project Tests/Phase1DCorrelation/Phase1DCorrelation.csproj
```

All candles and clocks are in memory. The harness makes no exchange/API requests.
