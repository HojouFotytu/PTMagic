# Analytics reliability harness

16 deterministic case groups cover the shared request timestamp under tight
retention, late/future response trimming, existing loader compatibility, missing /
inclusive-limit / expired / future decision clocks, exact ATR / volume /
correlation retention requirements, partial UTC trend periods, unknown settings,
same-window Concentration, and nonfatal insufficient-history diagnostics.

**All 16 case groups passed** in the user's .NET 10.0.403 run at source
`86c2ea3ff430b62e654f929a222935ec53ea8fa6`. Build and test execution remains
user-owned under D-021; configured runtime acceptance remains separate.

```sh
dotnet run --project Tests/Phase1AnalyticsReliability/Phase1AnalyticsReliability.csproj
```

The request-boundary tests use synthetic loaders and a fixed clock. No network,
real bot delivery, wall-clock delay, or market-data request is performed.
