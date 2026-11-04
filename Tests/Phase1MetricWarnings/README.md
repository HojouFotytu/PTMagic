# Raid metric-warning summary regression tests

Run:

```powershell
dotnet run --project Tests/Phase1MetricWarnings/Phase1MetricWarnings.csproj
```

Seven deterministic checks cover no-op healthy states, per-raid reset,
duplicate warning grouping, coverage diagnostics, asset failure counts,
unknown metric names, GlobalSetting hold messages, and five-detail capping.

The tests do not make exchange calls. Verify the end-of-raid output in
TestMode as an additional integration check.
