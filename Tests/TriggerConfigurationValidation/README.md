# Trigger configuration validation acceptance

Status: **40 checks passed in the user's .NET 10.0.403 run at source `86c2ea3ff430b62e654f929a222935ec53ea8fa6`.** This includes the corrected finite-volume fixture and both sentinel boundary cases. Native build and harness execution remain user-owned under D-021; this result applies to the recorded source and does not establish full-suite or configured runtime acceptance.

The user's run at source `78967a7703ab9f57fece40d488fc538b1caf393b` passed the first 23 checks, then exposed a fixture error in "timed and volume off-triggers need no trend". `Constants.Max24hVolume` is `double.MaxValue`; subtracting 1 rounds back to the same unbounded sentinel, so the row correctly required a trend reference. The fixture now uses a finite cap of 1,000. Two additional checks cover the adjacent representable value (`Math.BitDecrement`) as a volume cap and the exact sentinel as a negative control that still requires a trend. Production validation is unchanged.

The harness exercises `Core.Helper.TriggerConfigurationValidator.Validate`, `ThrowIfInvalid` and `GetMetricWarnings`. Custom activation expressions are validated through the production three-valued Boolean parser/evaluator.

Fatal configuration errors include missing or blank MarketTrend references in global activation, SMS activation and SMS off-trigger rows; duplicate case-insensitive trend/setting identities; duplicate nonblank tags; missing tags in custom activation expressions; malformed or unsupported expressions; and off-trigger connections other than AND/OR. Age/volume/time rows use their actual runtime precedence and do not require an unused trend reference.

The active-metric parameter contract is also checked: ATRPeriod and VolumeExpansionLookback 1–1000, TopNVolumeCount 1–1000, CorrelationLookback 3–1000, and finite nonnegative active breadth thresholds. Unused parameters do not invalidate legacy configurations.

Unknown, disabled and wrong-scope requested metrics deliberately remain **accepted diagnostic warnings** and resolve unavailable at runtime. Intentional invalid-metric diagnostic configurations therefore remain usable. A missing MarketTrend is a different structural error and is rejected before startup/reload/save proceeds; runtime defensive handling must also keep it unavailable.

From the PTMagic repository root, on the user's .NET 10 environment:

```powershell
dotnet run --project Tests/TriggerConfigurationValidation/TriggerConfigurationValidation.csproj
```

Expected final line after a successful run:

```text
Trigger configuration validation checks passed: 40.
```

Startup/reload wiring and runtime fallback behavior also require the orchestration acceptance harness and native TestMode run. This isolated validator harness does not substitute for those integration checks.
