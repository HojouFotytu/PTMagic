# Decision history regression harness

## Operator validation update — November 26, 2026 (historical code checkpoint)

The user executed `dotnet run --project .\Tests\DecisionHistory` after the unified retention + storage estimator + path-normalization fix: **all 17 checks PASS**, including legacy retention migration, no cumulative quota, JSONL segment integrity, MarketData-only cleanup, combined storage measurement and failure-safe records. The next user-executed `dotnet build .\PTMagic.sln` compiled Core, PTMagic and Monitor successfully in **13.3 seconds**. These are real operator outputs and supersede the earlier "native retest pending" notes for that checkpoint only. Subsequent November 26 FAILSAFE/watchdog and market analytics source changes are not covered by those executions and require a new build/lifecycle run. The longer history retention does not extend active normalized OHLCV beyond `StoreDataMaxHours`.


## November 24 — Fix migration-test save path

A .NET 10 run passed the first ten DecisionHistory checks then failed in the migration case: PTMagicConfiguration(string basePath) accepted an unseparated temp-directory path, while its write/backup helpers relied on a trailing directory separator. Both constructors now normalize the base path before any read/write, and the migration regression asserts the settings backup and subsequent Analyzer settings backup remain inside the supplied directory. Native retest of the amended commit pending.


## November 24 — Combined historical growth estimate

Added one combined size projection for legacy MarketData snapshots and DecisionHistory, excluding active OHLCV cache. The server measures file metadata in a rolling 48-hour observation window and reports observed daily growth, total existing history bytes, and available disk capacity, with five-minute caching on the Settings General page. The browser projects requested days immediately without further file scans and displays only combined estimated storage, free capacity, and observed daily growth. Fewer than one observed hour shows "Collecting data" rather than inventing a growth rate. Added a two-dataset offline estimator regression. .NET build and live UI verification pending.


## November 24 — Unified history retention

`Application.HistoryRetentionDays` (default 30 days) now controls historical MarketData snapshots and DecisionHistory JSONL. Removed DecisionHistory total size quota and its early deletion behavior; 16 MiB segment limits and age-based pruning remain. Older explicit DecisionHistoryRetentionDays is migrated only when the new key is absent. MarketData cleanup is restricted to MarketData snapshots and never truncates below the active StoreDataMaxHours requirement. Tests verify quota-free retention, migration, scope-limited cleanup and existing integrity. .NET build/test execution pending.


**Status: all 13 check groups passed in the user's .NET 10.0.403 run at source `d331fbd059041241069646c3bb82ac6549423a70`.** Core, PTMagic and Monitor built successfully in 10.9 seconds, and the ConfigurationLifecycle harness passed all 66 checks before this project ran. Under D-021 the user runs all native .NET validation locally.

The earlier validation at source `86c2ea3ff430b62e654f929a222935ec53ea8fa6` stopped on a `Telegram` type/namespace collision in ConfigurationLifecycle before this project ran. Both fixtures now explicitly construct `Core.Main.DataObjects.PTMagicData.Telegram`, and the successful continuation validates that correction.

The harness calls the production JSONL writer and acknowledgement checkpoint, and uses actual `PTMagic` capture/history methods with synthetic offline configuration. It covers complete append records, UTC date rotation, retention, interrupted tails, the 16 MiB segment bound, total quota pruning, oversized-record rejection, invalid limits, metadata-only acknowledgement persistence, TestMode separation, separate RelativeTrigger inputs for different SMS settings, failed-raid single-write capture, source provenance, and disabled history.

The bounded-file checks allocate temporary records up to 16 MiB. All fixture files are created beneath the operating system temporary directory and removed on completion. No exchange or ProfitTrailer requests occur.

From the repository root in PowerShell:

```powershell
dotnet run --project .\Tests\DecisionHistory\DecisionHistory.csproj --configuration Release
if ($LASTEXITCODE -ne 0) { throw "DecisionHistory failed." }
```

Passing this harness does not establish configured runtime capture, live delivery or cross-exchange acceptance. Keep its results tied to the tested commit and continue with the configured runtime phase in the current handoff.
