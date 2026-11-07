# Configuration delivery and raid lifecycle acceptance

Status: **66 checks authored; revised harness not yet executed.** Ten bootstrap and exchange-adapter regression groups were added after the user's earlier validation. Native .NET builds, harness execution and TestMode acceptance remain user-owned under D-021. The authored count is not a pass result.

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
- `POST api/v2/globalSellOnlyMode` with encoded `token` and `enabled` query values, preserved PT base/context paths, no property body, distinct additional-target tokens and the documented primary-token fallback.
- Startup control changes no PAIRS, DCA or INDICATORS content and does not advance or overwrite normal configuration acknowledgement metadata.
- Startup API guard persistence through indeterminate selection, stale raids and failed normal delivery, followed by the ordered sequence of normal configuration acknowledgement and `enabled=false`.
- Failed release keeps normal configuration confirmed and guard control pending. The next raid reasserts `enabled=true`, reevaluates fresh data and retries release without resending unchanged properties.
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

Expected final line after a successful run:

```text
Configuration lifecycle checks passed: 66.
```

This fixture does not execute the full scheduled raid loop, live exchange collection, a real ProfitTrailer server or Monitor Razor/JavaScript. Native build, TestMode observation and the documented browser acceptance remain required before claiming release readiness. Reflection intentionally couples these cases to the production methods; a method/field rename must update the fixture rather than replace it with a simulated algorithm.

The supplied API contract is a setter. These tests do not claim a getter/readback of pre-existing global SOM, preservation of independently enabled manual SOM, or native PT trade behavior. Enabling PTM's startup option gives PTM control of the global SOM startup lifecycle: request `true`, complete a fresh conclusive acknowledged configuration, then request `false`.
