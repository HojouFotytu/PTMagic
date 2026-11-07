# Monitor settings form acceptance

Status: **28 checks authored; not executed.** Native build and harness execution remain user-owned under D-021. These cases do not add to the previously confirmed test count until the user runs them successfully.

The harness links the production `Monitor/Configuration/AnalyzerSettingsForm.cs` used by `SettingsAnalyzerModel.OnPost`, together with the production `AnalyzerSettingsEditor.cs` row projection used by the AJAX pages and `AnalyzerSettingsStore.cs` file commit. It does not implement an alternate save algorithm. The fixture converts production row values and selected HTML options into posted form fields, then calls the same detached-copy validation and transformation used by the Monitor. Two isolated temporary-file cases cover atomic commit, same-revision rejection and failed replacement without mutation of the source object or original file.

Coverage includes full no-edit equality, multiple Metric/Tag rows sharing one trend, precise threshold edits, row deletion and ordering, multiple additions, interleaved SMS metric/age/volume rows, timed/volume/metric OffTriggers, inactive field preservation, checkbox transitions, unrendered settings, property types/formulas/suffixes, HTML-encoded names, non-English culture, stale revision and incomplete AJAX guards, duplicate/index-alias identities, repeated scalar fields, missing references, malformed expressions and several new settings in one form.

From the PTMagic repository root, on the user's .NET 10 environment:

```powershell
dotnet run --project Tests/MonitorSettingsRoundTrip/MonitorSettingsRoundTrip.csproj
```

Expected final line after a successful run:

```text
Monitor production settings roundtrip checks passed: 28.
```

## Native Monitor smoke test still required

Build the Monitor, open Analyzer Settings and use a configuration with at least two global activation triggers, two SMS activation triggers and two SMS off-triggers referring to the same MarketTrend but different Metric/Tag values. Include tagged age/volume rows and full-precision decimal thresholds. Save without editing and compare the parsed analyzer JSON; formatting/comments are intentionally rewritten by the existing configuration writer. Then edit the second shared-trend row, add two rows, remove one row and save again. Verify each intended row and every unaffected field.

Also verify renaming a referenced trend, adding a trend and selecting it before saving, adding the first SMS to an empty list, adding several settings before saving, removing every off-trigger/property, unchecked checkboxes, custom tagged Boolean expressions and a page loaded before an external analyzer-settings edit. The stale page must refuse to overwrite the newer file. A failed AJAX section must keep saving unavailable. The only default fallback cannot be removed and an unsupported Exchange timeframe cannot be saved. Confirm correlation values display without `%`, history warnings remain visible, and Status Summary distinguishes selected from confirmed applied configuration.

This console harness does not execute Razor rendering, browser JavaScript or the full live Monitor. Those remain explicit native acceptance gates. Atomic-file tests use only newly created temporary fixture directories.
