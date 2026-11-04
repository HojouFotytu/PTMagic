# Raid metric-warning summary regression tests

Run locally:

```powershell
dotnet run --project Tests/Phase1MetricWarnings/Phase1MetricWarnings.csproj
```

Twelve deterministic checks cover healthy output; per-raid reset and deduplication;
global coverage and detailed invalid feature metadata; grouped, mixed-reason
asset failures; unknown metrics; GlobalSetting indeterminate decisions;
all distinct warnings; full GlobalSetting/SMS activation/SMS OffTrigger caller
context; bounded examples; reason/source counts; and implicit PriceTrend labels.

The tests do not make exchange calls. The user runs .NET build, all harnesses
and Binance Futures/Spot/US TestMode locally (D-021).

TestMode acceptance:
- For FILTERED_1h, verify warning names the failing SMS setting, symbol and
  trigger and identifies IgnoredMarkets/AllowedMarkets/universe exclusion.
- Verify no per-pair log flood: metric groups count requests, preserve several
  examples, and report the number omitted beyond the example cap.
- Verify incomplete 8h metric warnings give validity, sample coverage,
  endpoint, missing-history reason, and example affected markets.
- Verify global asset-only metric references give GlobalSetting name and tag.
- Verify SMS OffTrigger warnings include market and activation/exit context.
- Verify existing trigger truth values and no PT writes in TestMode.

Prior verified baseline: 70/70 tests before this change. New target: 75/75
across the six existing harnesses, **not yet executed**.
