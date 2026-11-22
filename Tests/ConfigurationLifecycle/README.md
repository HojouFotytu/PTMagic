# Configuration delivery and raid lifecycle acceptance

## November 23 — AnalyzerState Linux startup and first-seen progress corrections (new checks pending)

The PTMagic constructor no longer creates the named `analyzerStateMutex`. `WriteStateToFile` now opens a persistent `_data/AnalyzerState.lock` file exclusively, with bounded retries under contention. The lock file remains on disk to avoid competing inode/recreation races. Existing `_data/AnalyzerState` name and integer contents are unchanged, while independent instances with different `_data` directories no longer share one global mutex. Linux processes without `/run/user/<UID>` can construct the analyzer without attempting named-mutex creation. Mixed old/new binaries sharing the same `_data` are not mutually synchronized: stop the old instance before deploying.

The first-seen-date check now writes a single live console progress line from prefix through periods and completion; the file DEBUG channel receives one summary, rather than an INFO prefix that ends the line before the dots. Interrupted progress also ends its line. The existing SMS progress behavior is unchanged.

**New development tests (authored, not executed in this environment):** production `WriteStateToFile` creates the state and persistent lock, waits while an independent file stream owns the lock, and succeeds after release; independent instance-directory behavior. Production `BinanceFutures.CheckFirstSeenDatesAsync` with entirely cached MarketInfo exercises the exact one-line console output with zero network; a pre-canceled token tests interrupted line closure. **User-local verification is pending**: build, ConfigurationLifecycle suite, Linux PM2 startup/restart without `/run/user/<UID>`, and visual progress. Do not record PASS until these are run.


## November 22 — Unexecuted GlobalSetting Delta selection regression

One additional focused acceptance case executes the production `CheckGlobalSettingsTriggers()` path against synthetic current Breadth=55 and previous Breadth=40. It verifies Delta=15 selection, unchanged explicit Absolute=55, Delta mismatch under absolute-only bounds, and missing historical Delta causing indeterminate selection instead of absolute fallback. Static runtime snapshots are restored after the test. The known older 84/84 PASS does not cover this case; run the current suite before claiming a new PASS.

## Current verification — 84/84 PASS on November 17, 2026

**Two owner-executed `ConfigurationLifecycle` runs: 84/84 PASS** after `70146fa` and again after [`303b2e7`](https://github.com/HojouFotytu/PTMagic/commit/303b2e7f98d33a5d9cb07c13cf016406a1a49804). An earlier full Core/PTMagic/Monitor build **PASS 16.0s** at `fd4aa2e`; the final `303b2e7` `dotnet run` is not a newly reported *complete solution* build. `70146fa` updated a stale Monitor assertion `Model.GlobalMetricGroups` → `Model.DisplayedGlobalMetricGroups` that had stopped the first 84-group attempt.

`fd4aa2e` removes per-market and per-SMS DEBUG, periodic ten-market INFO and per-SMS-name INFO summary counts. The entire SMS evaluation produces a **single incremental console progress line**, one dot per 25 completed markets, and one DEBUG file-log summary. `303b2e7` removes separate `SettingsHandler` compilation dots. Regression checks prove one line with 51 markets (three dots) and again for an **activated, property-compiling SMS**; both PASS in the 84-group suite. Multiple progress lines in the test transcript come from **separate test cases**. All decision/trigger/compilation logic remains unchanged. Latest production runtime of the consolidated output is still pending.

**Authoritative global SOM:** enable-only; PTMagic never sends `enabled=false`. User manually releases SOM in ProfitTrailer. Older 66/79-check and automatic-release descriptions below are historical and superseded.




Status: **66 checks passed in the user's .NET 10.0.403 run at source `d331fbd059041241069646c3bb82ac6549423a70`.** Core, PTMagic and Monitor built successfully in 10.9 seconds, then all lifecycle checks and all 13 DecisionHistory checks completed. This resolves the earlier fixture compilation failure at `86c2ea3`. Together with the 193 passes from ten unchanged harnesses at that earlier revision, all 272 groups have passed across the two recorded runs. This is not a claim of a single twelve-harness run at `d331fbd`; configured runtime acceptance remains separate under D-021.

**November 10 SOM safety update:** Startup global SOM is now enable-only, with manual release in PT. The test harness has been revised to reject all automatic `enabled=false` API calls, migrate v1 release checkpoints, prevent repeated commands after unconfirmed enables, and verify that the raid-1/raid-2 warning flag clears at raid 3 and rearms on a new startup. These revised tests have **not** yet been run; previous 66/66 lifecycle evidence belongs to the earlier source.

The harness calls the production `ConfigurationDeliveryCoordinator`, `GlobalSellOnlyModeAPI`, `SettingsAPI`, `SettingsHandler` and the public Binance, Binance Futures and Binance US exchange adapters. It also invokes the actual PTMagic private orchestration methods through reflection: initialization, preset bootstrap, startup API guard delivery and release, final configuration save, SMS evaluation, global trigger selection, SMS summary persistence and mode changes. No alternate delivery/SMS algorithm is implemented in the tests.

ProfitTrailer requests use an injected `HttpMessageHandler`; every request must address one of two `.invalid` fixture hosts. The fake handler records method, URI, query and body, and supplies success, unauthorized, server-error, redirect, HTML/login, explicit API-failure and immediate timeout outcomes. No network request or real delay is involved. Each orchestration fixture writes settings, presets and checkpoints into a unique temporary directory, pins the production raid observation clock, then restores the previous working directory and static pin before deleting its fixture files.

Exchange-adapter boundary calls always receive an already-canceled `CancellationToken`. Valid USDT identity prices (and USD on Binance US) return 1 without collection; invalid asset codes must raise `ArgumentException` for `mainMarket` before any I/O or Futures metadata refresh. Valid quotes that need conversion must reach cancellation rather than silently return an identity price. The canceled token prevents network transmission if the input/identity boundary regresses; these cases do not fetch market data.

Coverage includes:

- Exact, case-insensitive PAIRS `market` parsing, whitespace trimming, uppercase normalization and preservation of non-USDT quote currencies. Comments and market-prefixed keys are ignored; missing, null, blank, malformed and non-ASCII values raise an actionable PAIRS/market error instead of defaulting to USDT.
- Null PAIRS, DCA and INDICATORS property groups inherit actual default-preset assignments. A fresh summary obtains the selected preset's quote before exchange loading, with and without a prior acknowledged configuration, without sending settings or changing the acknowledgement checkpoint.
- A missing market during bootstrap preserves an owned startup SOM guard and its checkpoint, leaves the first successful raid pending, and sends no normal configuration or SOM release.
- Public exchange price/data adapter input boundaries, USDT identity on all three exchanges, and the Binance US-only USD identity.
- Success, failure, exception, missing acknowledgement, same-payload retry, unchanged suppression, forced replay and destination changes.
- Confirmed A → partial B failure → desired A replay; a failed partial transfer cannot use the old confirmed hash to skip recovery.
- Complete multi-target HTTP acknowledgement and expiration before the next target/retry.
- Bounded HTTP 503 retries (2s, 5s, 10s; 17s total wait), response Retry-After handling, freshness checks between attempts, and no immediate retry when the server asks for a delay beyond the budget. Deterministic injected waits avoid real sleeps; HTTP 401/500 and timeout handling retain their existing behavior.
- `POST api/v2/globalSellOnlyMode` with encoded `token` and `enabled` query values, preserved PT base/context paths, no property body, distinct additional-target tokens and the documented primary-token fallback.
- Startup control changes no PAIRS, DCA or INDICATORS content and does not advance or overwrite normal configuration acknowledgement metadata.
- Startup SOM enable-only guard persistence through indeterminate selection, stale raids and failed normal delivery; no automatic `enabled=false` after acknowledgement.
- Failed enable remains reviewable; no automatic OFF/release retry can override manual PT SOM state.
- Freshness expiration during normal configuration delivery prevents the subsequent release command. Missing API credentials, mismatched token lists and partial target failures cannot be reported as complete acknowledgement.
- Separate metadata-only startup guard checkpoint, restart recovery, opt-out behavior with and without an outstanding owned operation, and TestMode isolation of both API calls and live checkpoint files.
- Removal of the final SMS after the preset has already been rebuilt without SMS markers; the actual SMS evaluator and configuration sender must still deliver the clean payload.
- Failed setting switch restoration, same-name changed-property retry, and selected versus applied summary fields.
- TestMode transport/checkpoint isolation and separate preview SMS persistence; switching live reads the live latch only.
- Missing/nonfinite volume, missing/sentinel/future age observations, retention of an existing SMS on unknown activation, missing off-trigger operands under AND/OR, and negated missing global references.

From the PTMagic repository root, on the user's .NET 10 environment:

```powershell
dotnet run --project Tests/ConfigurationLifecycle/ConfigurationLifecycle.csproj
if ($LASTEXITCODE -ne 0) { throw "ConfigurationLifecycle failed." }
```

Expected final line after a successful run of this revision:

```text
Configuration lifecycle checks passed: 84.
```

The historic 66/79-group evidence is superseded by the user-run 84/84 PASS at the current checkpoint above.

This fixture does not execute the full scheduled raid loop, live exchange collection, a real ProfitTrailer server or Monitor Razor/JavaScript. The native build and harness have passed at the recorded source; configured TestMode observation, PT integration and the documented browser acceptance remain required before claiming release readiness. Reflection intentionally couples these cases to the production methods; a method/field rename must update the fixture rather than replace it with a simulated algorithm.

The supplied API contract is a setter. These tests do not claim a getter/readback of pre-existing global SOM, preservation of independently enabled manual SOM, or native PT trade behavior. Enabling PTM's startup option requests `true` only; PTMagic never requests `false`. The user manually releases global SOM in ProfitTrailer.
