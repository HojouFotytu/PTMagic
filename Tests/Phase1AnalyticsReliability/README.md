# Analytics reliability harness

16 deterministic case groups cover the shared request timestamp under tight
retention, late/future response trimming, existing loader compatibility, missing /
inclusive-limit / expired / future decision clocks, exact ATR / volume /
correlation retention requirements, partial UTC trend periods, unknown settings,
same-window Concentration, and nonfatal insufficient-history diagnostics.

This is source-authored coverage. It has **not been executed** in this work;
build and test execution remains user-owned under D-021.

```sh
dotnet run --project Tests/Phase1AnalyticsReliability/Phase1AnalyticsReliability.csproj
```

The request-boundary tests use synthetic loaders and a fixed clock. No network,
real bot delivery, wall-clock delay, or market-data request is performed.
