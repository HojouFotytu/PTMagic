# Phase 1A readiness regression

Run the deterministic, exchange-independent checks:

```bash
dotnet run --project Tests/Phase1AReadiness/Phase1AReadiness.csproj
```

The executable uses no additional NuGet test packages. It verifies the
90% boundary, empty-population rejection, missing exact candle endpoints,
missing historical references, cross-sectional fail-closed behavior,
independently valid BTC-relative strength, recovery on successive incremental
cleanup raids, and the 100% volume-ranking requirement for Concentration.

**Production verification remains necessary:** compile the full solution,
then execute a cold-start and successive raids with
`GeneralSettings.Application.TestMode=true`, checking the per-trend coverage,
invalid-reason counts, retained GlobalSetting when normalized data is absent,
and recovery without redundant full-history requests. Also exercise valid
independent GlobalSetting triggers and negated/custom trigger expressions.
There is no automatic trading suspension in this change.
