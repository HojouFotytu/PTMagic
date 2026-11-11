# Phase 2 — Binance Futures Global Account Long/Short Ratio Checks

Focused offline checks for the single opt-in public Binance USD-M Futures
global-account long/short ratio feed. No API key or API secret is required. As of D-051.1, the ratio request
budget is no longer user-configurable. A temporary internal 60-page-per-raid
ceiling remains until adaptive derivative acquisition replaces it. Legacy
budget keys are ignored; acquisition is controlled by explicit metric selection.

Run from the PTMagic repository root:

```powershell
dotnet run --project .\Tests\Phase2LongShortRatios\Phase2LongShortRatios.csproj
```

No live Binance calls, PT instance, or trading actions are required.
