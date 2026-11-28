# PT history exporter: verified contract and rebuild

## November 28 — Single-click automatic multipart download (current UI)

Operator live acceptance of the manual interface: 24-hour GeorgeLong export, 2,430 / 2,430 files, two finalized ZIPs (449.05 and 169.26 MiB) under a 450 MiB limit. User explicitly rejected the individual file list, next-part button and manual release workflow. Both download buttons now automatically request every numbered attachment as each part is prepared. Browser-native download manager handles the actual file transfer (no JavaScript ZIP blobs, no client-side archive buffering); the page only displays compact progress, errors and indivisible oversize warnings. It reports downloads requested rather than confirmed saved; browser multiple-download permission may be required and cannot be bypassed. The maximum-size and independent ZIP contracts remain unchanged.

Retain prepared parts for browser retry/range requests until existing 30-minute idle expiry or shutdown. New explicit same-session export can supersede an unfinished preparation without deleting published parts; a completed export does not block a later request from another session. Only one active preparation cursor; all retained parts remain owner/instance bound and immutable. Explicit release API remains compatible but no manual release UI. Added lifecycle regression for supersession, cross-session protection, earlier-part retry and cleanup. Automatic browser dispatch and native validation of this follow-up pending.


## November 28 — size-limited multipart Logs/Data ZIP exports

**Operator native validation — November 28, 2026:** Source `034539d8749339be4776b44cf74f5f137808eea1` built Core, PTMagic and Monitor successfully in 10.9 seconds. PTDataExport passed all 37 test groups, including async streaming/backpressure, exact DecisionHistory export ranges, finalized multipart size limits, complete reverse-order dataset reconstruction, explicitly oversized indivisible sources, ZIP overhead and cancellation/source-loss retry. This validates the offline harness and solution build, not deployed authentication, live browser multipart preparation/download/retry, or live server spool resource usage. Those acceptance checks remain pending.

The shared Logs/Data history form includes Maximum ZIP size (MiB), a nonnegative whole number. Zero retains the existing single direct-streaming download. A positive value starts an authenticated multipart export, capturing the source membership and UTC range once, with deterministic ordering and unchanged source filters. One ZIP is prepared at a time; the first part is prepared immediately and subsequent parts are prepared with the next-part control. Each is an independently readable ordinary ZIP with unchanged relative entry paths, shared instance/time/export ID and sequential part number; extraction into one folder in any order reconstructs the selected membership. No split RAID or log file, no duplicate source paths, and no changes to DecisionHistory storage or retention.

Source byte lengths plus ZIP metadata overhead provide conservative packing only. The finalized ZIP length is authoritative: if too large, retry a smaller source prefix; if an individual source's actual ZIP exceeds the requested limit, publish it separately with an explicit oversized warning and filename. Parts may be smaller than the limit, particularly compressible logs; the cap is not a minimum or a promised optimal number of parts. Missing sources fail rather than silently disappear; finite legacy segments with no complete in-window records are legitimately skipped. Append-only boundaries and legacy record filtering retain existing semantics; mutable snapshots are read when their part is prepared, not a transactional whole-instance snapshot.

One active export per Monitor; session/instance-bound access, POST antiforgery on state changes and password-protected downloads. Prepared immutable parts in private Monitor _download_exports spool (not _data/wwwroot) can be downloaded again, including HTTP ranges. Explicit release, 30-minute inactivity expiry (matching Monitor session timeout), or Monitor shutdown cleans the spool; abandoned folders are reclaimed after expiry with lease checking. Disk overhead grows with prepared parts until release; each part is made available without waiting for the entire export. No automatic multiple browser downloads. Native regression passed as recorded above; actual browser tests pending. Regression covers exact finalized caps, independent reverse-order extraction, stable retry, oversized member isolation, overhead, cancellation and source-loss recovery.


## November 27 — exact DecisionHistory export window

Schema 2 compressed single-raid files are selected by their exact UTC RecordedAtUtc filename and copied intact. Legacy daily segments are scanned with bounded buffers and only complete records within the inclusive UTC time window are exported; no entire record is loaded into Monitor memory. All available retains complete legacy lines; in-progress .tmp files are excluded. Existing cache, log and Exchange snapshot policies unchanged. Regression covers exact tick boundaries, old and new formats, huge legacy records and interrupted appends. Native/browser validation pending.


## November 27 — ZIP finalization synchronous-write correction

Owner native validation of `b940b369` succeeded for the full solution build (16.5s) and first 17 PTDataExport test groups, then the async-only destination rejected `DeflateStream.Dispose` synchronous writes during ZIP entry disposal. This is a real incompatibility with async-only HTTP response streams, not a size limit. The replacement isolates all synchronous ZIP/file operations on a dedicated producer thread and transfers pooled 64 KiB chunks through a four-slot bounded channel to an exclusively asynchronous HTTP consumer. Cancellation and downstream failure cancel and join the producer, returning any queued buffers. No temporary ZIP, synchronous HTTP I/O, whole-archive buffering or change to range/retention rules. Existing async-only ZIP regression stays unchanged in its prohibition of synchronous writes; added large stored entry, slow output, cancellation and downstream-failure cases. Native execution and browser acceptance pending.

## November 27 — Streaming PTMagic Logs/Data exports

Both ZIP buttons submit the shared history period. Logs retain the existing date filter; `_data` filters DecisionHistory by UTC segment date (whole boundary day) and Exchange MarketData snapshots by UTC filename timestamp. Current snapshots, metadata and complete OHLCV/funding caches remain included. All available and legacy links without a range retain all files. Shared .NET 10 asynchronous non-seekable ZIP writer starts the HTTP response before compression, does not create a temporary archive, stores already compressed files without recompression, bounds active logs and JSONL by captured length, and trims incomplete JSONL suffixes. Other files use their opened length so atomic snapshot replacements are not truncated to an old length. Preserve authentication, instance filenames and link exclusion. A post-header failure aborts the response and is logged; native and browser testing pending. No retention or trading changes.


## November 25–26 export range update and operator acceptance

The PT Data page now uses an integer field plus Days/Weeks/Months/All available dropdown rather than the earlier fixed 7d/1m/3m/6m selection. The shared `ExportHistoryPeriod` parser retains legacy range codes; months are calendar-month `AddMonths` computations. Buy/sell source pages, raw source records, original one-based API pagination and schema 1.3 exporter manifest are unchanged. The PTMagic Logs page uses the same range pattern but ZIPs whole retained daily log files, including the boundary date; undated files use modification time. The separately downloadable PTMagic `_data` ZIP is always complete, without a range filter. These are Monitor export interfaces, not changes to native ProfitTrailer's trade-history retention.

**User-observed:** `dotnet run --project .\Tests\PTDataExport` completed **28/28 test groups PASS**, including custom/invalid day-week-month periods and log ZIP date filtering. This validates the test harness on that checkpoint, not live browser ZIP selection or newer FAILSAFE engine changes. Subsequent source must be built and browser-checked independently. Under the Monitor Control menu, the log/data page is now labeled `PTMagic Logs/Data` and appears directly before `PT Data`.


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

Date ranges remain 7d/1m/3m/6m/all. Finite ranges filter buy records using `lastBoughtDate` and sales using `soldDate`, with a fixed UTC upper bound. **For bounded selections, collection stops after processing the first descending page containing a transaction older than the cutoff**, rather than scanning older pages to reconstruct per-asset history. Older records on that page are excluded; raw page evidence is retained. `All available` still pages to the end. PT's database sort fields are `bought_date` and `sold_date`; the mapping of `bought_date` to the exposed `lastBoughtDate` for all buy types remains a live acceptance qualification. Time, byte, and page limits produce explicit partial status.

HTTP 429/503: up to three attempts on the same page. No redirect following with the production client. Per-response 32 MiB, shared downloaded-source budget 128 MiB, 10,000 JSON pages per history, per-request 25 seconds, export 120 seconds. Disk-backed aggregate and ZIP avoid holding entire history in memory. Per-page safe diagnostics include page, size, offset, status, attempts, length, SHA-256 and outcome, never token/host/full URL or exception text. Caller cancellation aborts and releases the gate.

Current positions and optional funding keep their original single-response behavior. Funding is not claimed to be fully paginated history. `/api/v2/data/misc`, PBL, strategies, Analyzer and flags are outside scope.

## Validation boundaries

`dotnet run --project Tests/PTDataExport/PTDataExport.csproj --configuration Release` compiles the actual production source with only connection settings stubbed. Tests now emulate the independently inspected one-based contract, not the previous client's zero-based assumption. Boundary groups exercise both endpoints at 0/1/9/10/11/20/21/400 records, original JSON, repeated/changed/overlapping pages, failures, UTC filtering, bounded buy/sell cutoff and unchanged All-available traversal, size/byte/page limits, cancellation and unchanged snapshots.

Live acceptance still requires a newly built Monitor download showing `pt-history-v3`, first API pages 1 and 2 with advancing records, correct counts/range and unchanged position snapshots. No synthetic run proves live acceptance. `range_coverage_complete` means the bounded scan terminated without detected uncertainty, not an atomic snapshot or full account lifetime history.

## Recorded validation — 2026-11-15

Fresh implementation commit: `7580a96c3d5ea4242959a6e26244c6aaf7826b50` on the existing `phase1/market-data-foundation` branch.

The initial rebuild run `43679795101` failed in the transfer-budget test after the first 20 groups passed. The test used a fixed 4,000-byte budget but incorrectly expected only one retained page even though two serialized fixture pages fit. This was a test expectation defect, not evidence of a production transfer-limit failure; the Monitor build step was skipped in that run.

Test correction `e63170fa12e8eb263e4880003fc384ca3ea0ecf6` derives the budget from the actual first response byte count plus one. It verifies that page 1 is retained, page 2 exceeds the cumulative budget, the archive remains valid and the dataset is partial. No production exporter behavior changed in that correction.

[Workflow run 43694100671](https://github.com/HojouFotytu/PTMagic/actions/runs/43694100671), job `132750725594`, tested commit `e63170fa12e8eb263e4880003fc384ca3ea0ecf6`:
- **PASS: all 25 HTTP-to-JSON-to-ZIP test groups.** These compile the production collector against simulated HTTP responses.
- **PASS: .NET 10 Monitor Release build, 0 warnings and 0 errors**, elapsed 13.46 seconds. This was a Monitor project build, not a full-solution build.
- **Live ProfitTrailer export / browser acceptance: not yet verified.** No live bot requests or deployment were performed by this validation.

These results validate the fresh collector and corrected tests, not the earlier zero-based exporter or an installed Monitor instance. Do not report live pagination fixed until a new real ZIP confirms advancement.


## November 15 — user-directed date-range correction

Only transactions within the selected 7d, 1m, 3m, or 6m range are included. A DCA transaction six months ago is excluded from a seven-day export, regardless of the position's age. Bounded history retrieval stops when the sorted API page reaches the date cutoff; it no longer scans older history for per-asset completeness. `All available` retains full pagination. No change to the UI, endpoints, position snapshots, or funding behavior. These semantics were validated by the 26/26 CI test groups and Monitor Release build in [run 43695709351](https://github.com/HojouFotytu/PTMagic/actions/runs/43695709351). Live PT acceptance remains pending.