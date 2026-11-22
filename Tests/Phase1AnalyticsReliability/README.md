# Analytics reliability harness

## November 23 — Concentration uses the top ten available markets and 90% coverage

Owner-approved policy: require only the normal **90% valid / fixed eligible cohort** threshold for `Concentration`, rather than complete 100% ranking and returns. Exclude missing/invalid return or quote-volume markets from ranking; select the Top10 of remaining source-valid markets and compare their average return against the remaining valid candidates. Keep the original eligible denominator, explicit sample counts and minimum of eleven valid ranked markets. No ranking reconstruction or predicted rank for excluded markets. The other complete-population volume metrics (e.g. HHI/share families) are not changed by this specific correction.

New real-engine regression `TestConcentrationUsesAvailableTop10AtNinetyPercentCoverage` checks 11 valid of 12 (including an excluded high-volume market with missing return reference) and invalid 11 of 13. New .NET build and focused test execution remain pending.


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
