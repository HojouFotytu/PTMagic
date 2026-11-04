# Phase 1 tri-state trigger regression harness

Run:

```powershell
dotnet run --project Tests/Phase1TriggerLogic/Phase1TriggerLogic.csproj
```

Covers strong Kleene three-valued AND/OR/NOT, nested and negated custom
connections, exact tag parsing, settings with unresolvable metrics,
positive OR branches, and SMS OffTrigger outcomes.

Source: Core/MarketAnalyzer/MarketData/ThreeValuedTriggerLogic.cs.

No exchange calls are required. Verify full application integration separately
in TestMode; passing this deterministic harness alone is not a live raid test.
