# Phase 2 — USD-M historical settled funding metrics

Binance `GET /fapi/v1/fundingRate` returns settled native decimal funding rates, settlement times, `rateType` (Regular/Special), and optional mark price. The history endpoint is separate from `premiumIndex.lastFundingRate`, which is not historical settlement data. Binance's ascending historical pagination uses inclusive `startTime`/`endTime` and up to 1000 records per page.

**Explicit asset metrics:** `FundingRate` = latest Regular funding settlement * 100 in percentage points, within the user's MarketTrend.TrendMinutes. `FundingSum` = sum of Regular funding settlements * 100 in percentage points across that window. They do not assume eight-hour intervals, extrapolate, annualize, or combine Special events with Regular events. Missing latest Regular funding makes FundingRate unavailable; a fully verified empty period yields a valid zero for FundingSum.

**History:** All required history derives from the longest enabled funding MarketTrend, not a fixed retention-hours setting. Binance provider availability and funding timetable determine whether records exist. The collector tracks verified coverage of fully exhausted provider responses and leaves partial or failed fetches unavailable. As of D-051.1, no user-configurable funding request budget exists. Production uses shared Binance rate-limit and freshness-aware admission; bounded overloads remain for deterministic tests. Old budget keys (including zero) are ignored. Pages have a maximum of 1000 records, two pages per symbol per raid. No funding requests occur unless a funding metric is explicitly enabled on Binance USD-M Futures. Spot and Binance US remain unsupported for this data family.

**Bulk funding acquisition:** Production first uses the symbol-omitted funding history endpoint over the exact pinned window. An exhausted page certifies every selected symbol, including a verified absence of settlements. Full pages certify only earlier-than-last timestamps and repeat the shared timestamp on the next page. Dense timestamp saturation, malformed pages, or other incomplete ranges fall back to the existing per-symbol acquisition. Raw page count (before deduplication), source identity, exact boundaries, and durable coverage proofs remain authoritative; no settlement is inferred. The existing Binance funding-rate limiter and freshness deadline remain active. The legacy per-symbol service API remains available for offline coverage.

The metric registry, normalized engine, SMS trigger/off-trigger path, metric exports, diagnostics, and Monitor History Requirements use PTMagic's existing shared pipeline.

**November 12, 2026 implementation (not yet locally compiled):** Six additional offline groups cover symbol-omitted URLs and mapping, exhausted-page full-population proof, shared timestamp pagination, dense-timestamp fallback, malformed-response fallback, and persisted proof reuse across restart. Run the focused Phase2Funding harness and the .NET 10 solution build before deploying.

**Verified November 10, 2026:** All **17 funding groups PASS** at source `49f2aab66592b6e2922097ca5242b5a614090602`; the solution built successfully in **11.0s**, and **16/16 offline harnesses passed (332/332 total groups)** using .NET SDK 10.0.403. Eight nullable warnings in two existing test projects were nonfatal. The earlier OI-only 315/315 result belongs to `5f51faaa`. Live Binance and configured PTMagic/Monitor acceptance remain separate.

From the PTMagic application clone, run:

```powershell
git switch phase1/market-data-foundation
if ($LASTEXITCODE -ne 0) { throw "git switch failed" }
git pull --ff-only
if ($LASTEXITCODE -ne 0) { throw "git pull failed" }
& ".\Tests\Phase2OpenInterestIncremental\Run-Phase2Validation.ps1"
```

The retained D-053 runner builds the solution and executes **15 offline test projects**, with no configured PT writes or browser actions. The 18-project results at `d101b5a0` predate D-053 and do not validate the current source.

## D-053 — retained family

The open-interest and long/short positioning source families were withdrawn. Retain native settled funding, bulk-first collection, per-symbol fallback, persistence, provider quota handling and exact metric availability. This remains an active metric family.

**D-053 source audit:** The original Unicode-safe symbol validator was shared with the deleted OI class; it was moved to `FuturesFunding.NormalizeSymbol` by `02820822`, with the funding feature-engine reference repaired by `d7847c46`. The retained funding logic, bulk/fallback collection and 28 existing offline case groups were reviewed. [Complete audit](https://github.com/HojouFotytu/PTM_2.10_Roadmaps_Handoffs/blob/main/updates/2026-11-12-d053-removal-code-audit.md). No post-removal local build/test PASS is claimed.
