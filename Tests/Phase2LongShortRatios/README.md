# Phase 2 — Binance Futures Long/Short Ratio Checks

Focused offline checks for the three opt-in Binance USD-M Futures ratio feeds:
global account, top-trader account, and top-trader position.

Run from the PTMagic repository root:

```powershell
dotnet run --project .\Tests\Phase2LongShortRatios\Phase2LongShortRatios.csproj
```

No live Binance calls, API credentials, PT instance, or trading actions are required.
