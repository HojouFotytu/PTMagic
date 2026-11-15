# PT history exporter: verified contract and rebuild

Date: 2026-11-15 KST. Scope: Monitor Control > PT Data only.

## Evidence, not an inferred API convention

Official release inspected: `taniman/profit-trailer`, tag `2.6.1`, asset `ProfitTrailer-2.6.1.zip`.
SHA-256: `ab2b63e5da061b9e7029cfb4060b5ffd7da9d4615f891bbdf9f0f3275b47a6a3`.
Inspection used `javap` on classes extracted from the release's `ProfitTrailer.jar`; it did NOT launch ProfitTrailer, connect to a bot, or access credentials.

Reproducible inspection commits and workflow runs:
- Controller: `6f5fb9c176e4e9d363f4ae1b889fb2d9ce3d7aa6`, [run 43679026647](https://github.com/HojouFotytu/PTMagic/actions/runs/43679026647), job 132705400656.
- Delegation: `897113f40ee08d471fa0ea3d2b92119c673d866c`, [run 43679322733](https://github.com/HojouFotytu/PTMagic/actions/runs/43679322733), job 132706288024.
- Actual offset: `20c33ba15e50e93b8cf102858be1f1f8f08f4b72`, [run 43679438732](https://github.com/HojouFotytu/PTMagic/actions/runs/43679438732), job 132706633344.
The temporary inspection steps are removed from normal CI after the rebuild; the historical commits retain the exact scripts. Normal tests/build no longer download the release.

Call chain:
`ApiV2Controller.buyLog/salesLog` -> `DataLogService.getBuyLogJson/getSellLogJson` -> `getBuyLog/getSellLog` -> `RepositoryService.getAllPositionHistorySearchResults` -> `PositionHistorySearchService.getAllPositionHistorySearchResults`.

The controller accepts `page`, `perPage`, `searchTerm`, `sort`, `sortDirection` (and token/hashcode). Although its default page is **0**, the history service passes `perPage` and **`perPage * (page - 1)`** to its repository. At bytecode offsets 351-360: load perPage; load perPage; load page; constant 1; subtract; multiply; invoke repository.

Therefore history traversal is **page 1, 2, 3...**, not 0, 1, 2. At size 10, correct offsets are 0, 10, 20. The old first request generated offset -10, followed by offset 0. The user's schema-1.2 archive independently showed those first two requests returning the same first ten records. This establishes a client indexing defect and explains the observed pattern; it is NOT a claim that a rebuilt live bot has been tested. The test's negative-offset clamp models the supplied alias evidence; it is not asserted to be bytecode in the service.

PT returns a `{hashcode,data}` envelope. `hashcode` is a cache hint, not a paging cursor. Do not send it for collection; HTTP 204 is not an empty-history page. History JSON IDs come from `PositionHistoryEntity.getId()`, not the current-position display counter. `firstBoughtDate`, `lastBoughtDate`, and `soldDate` are serialized as UTC epoch seconds.

## Fresh implementation

`PTDataExportService.cs` is replaced with a one-pass collector, retaining its public integration API and the existing selection UI, configured-target authentication, per-instance filename integration, snapshot endpoints and funding opt-in.

Manifest **1.3**, exporter **pt-history-v3**. Start at page 1 with 10 records. Preserve raw JSON immediately, append complete original record JSON without field mapping, then advance. On actual response-size overflow, reduce **10 -> 5 -> 1** at the same row offset. New page = offset / newSize + 1. No restart/replay scan and no guessed endpoint alternatives.

Raw responses: `Trades/{buys|sales}-pages/page-NNNNN-size-NN.json`.
Combined files remain `Trades/buys.json` and `Trades/sales.json`, `{data,sourcePageHashcodes}`.

Record identity uses history id when available, otherwise exact content hash. The first observation is retained in combined JSON. Every raw version remains available. Overlap, changed duplicate IDs and undated rows in a finite window make coverage partial. A page containing no new identities stops with `history_page_repeated`, even if mutable ticker fields changed. If a server exceeds the requested page size, all returned records are retained but coverage is partial: the next offset must not be invented.

Date ranges remain 7d/1m/3m/6m/all. Finite ranges filter buys on lastBoughtDate and sales on soldDate, with a fixed UTC upper bound. **This rebuild scans to the terminal short/empty page, then applies filtering during collection; it does not early-stop at the first old buy.** The database search field `bought_date` was observed, but its equivalence to lastBoughtDate for all initial/DCA history types was not established. Prefer a complete bounded scan over a speculative cutoff that can omit recent DCA transactions from older positions. Raw evidence pages can contain out-of-range records. Time/byte/page limits remain explicit and produce partial status, never false completeness.

HTTP 429/503: up to three attempts on the same page. No redirect following with the production client. Per-response 32 MiB, shared downloaded-source budget 128 MiB, 10,000 JSON pages per history, per-request 25 seconds, export 120 seconds. Disk-backed aggregate and ZIP avoid holding entire history in memory. Per-page safe diagnostics include page, size, offset, status, attempts, length, SHA-256 and outcome, never token/host/full URL or exception text. Caller cancellation aborts and releases the gate.

Current positions and optional funding keep their original single-response behavior. Funding is not claimed to be fully paginated history. `/api/v2/data/misc`, PBL, strategies, Analyzer and flags are outside scope.

## Validation boundaries

`dotnet run --project Tests/PTDataExport/PTDataExport.csproj --configuration Release` compiles the actual production source with only connection settings stubbed. Tests now emulate the independently inspected one-based contract, not the previous client's zero-based assumption. Boundary groups exercise both endpoints at 0/1/9/10/11/20/21/400 records, original JSON, repeated/changed/overlapping pages, failures, UTC filtering, late DCA dates, size/byte/page limits, cancellation and unchanged snapshots.

Compile/test success must be recorded from the rebuild's own CI run, not the inspection runs above (those still ran the old exporter). Live acceptance still requires a newly built Monitor download showing `pt-history-v3`, first API pages 1 and 2 with advancing records, correct counts/range and unchanged position snapshots. No synthetic run proves live acceptance. `range_coverage_complete` means the bounded scan terminated without detected uncertainty, not an atomic snapshot or full account lifetime history.
