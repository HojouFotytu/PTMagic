# Decision history regression harness

**Status: 13 source-authored check groups; not executed.** Under D-021 the user runs all native .NET validation locally.

The user's validation at source `86c2ea3ff430b62e654f929a222935ec53ea8fa6` stopped on a `Telegram` type/namespace collision in ConfigurationLifecycle before this project ran. Source review found the same unqualified constructor in this harness. It now explicitly constructs `Core.Main.DataObjects.PTMagicData.Telegram`; the correction and all 13 groups still need native validation.

The harness calls the production JSONL writer and acknowledgement checkpoint, and uses actual `PTMagic` capture/history methods with synthetic offline configuration. It covers complete append records, UTC date rotation, retention, interrupted tails, the 16 MiB segment bound, total quota pruning, oversized-record rejection, invalid limits, metadata-only acknowledgement persistence, TestMode separation, separate RelativeTrigger inputs for different SMS settings, failed-raid single-write capture, source provenance, and disabled history.

The bounded-file checks allocate temporary records up to 16 MiB. All fixture files are created beneath the operating system temporary directory and removed on completion. No exchange or ProfitTrailer requests occur.

From the repository root in PowerShell:

```powershell
dotnet run --project .\Tests\DecisionHistory\DecisionHistory.csproj --configuration Release
if ($LASTEXITCODE -ne 0) { throw "DecisionHistory failed." }
```

Passing this harness does not establish live delivery or cross-exchange acceptance. Keep its results tied to the tested commit and run the full handoff validation sequence.
