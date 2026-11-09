# Phase 2 — USD-M historical settled funding metrics

Binance `GET /fapi/v1/fundingRate` returns settled native decimal funding rates, settlement times, `rateType` (Regular/Special), and optional mark price. The history endpoint is separate from `premiumIndex.lastFundingRate`, which is not historical settlement data. Binance's ascending historical pagination uses inclusive `startTime`/`endTime` and up to 1000 records per page.

**Explicit asset metrics:** `FundingRate` = latest Regular funding settlement * 100 in percentage points, within the user's MarketTrend.TrendMinutes. `FundingSum` = sum of Regular funding settlements * 100 in percentage points across that window. They do not assume eight-hour intervals, extrapolate, annualize, or combine Special events with Regular events. Missing latest Regular funding makes FundingRate unavailable; a fully verified empty period yields a valid zero for FundingSum.

**History:** All required history derives from the longest enabled funding MarketTrend, not a fixed retention-hours setting. Binance provider availability and funding timetable determine whether records exist. The collector tracks verified coverage of fully exhausted provider responses and leaves partial or failed fetches unavailable. `FundingMaxRequestsPerRaid` is a separate request budget, default 60, range 0–400; 0 disables funding acquisition. Pages have a maximum of 1000 records, two pages per symbol per raid. No funding requests occur unless a funding metric is explicitly enabled on Binance USD-M Futures. Spot and Binance US remain unsupported for this data family.

The metric registry, normalized engine, SMS trigger/off-trigger path, metric exports, diagnostics, and Monitor History Requirements use PTMagic's existing shared pipeline.

**Testing:** 15 new offline case groups authored, not yet executed. The earlier 315/315 offline pass was for open-interest source `5f51faaa`, not this funding implementation. Live exchange and configured PTMagic/Monitor acceptance remain separate.

From the PTMagic application clone, run:

```powershell
git switch phase1/market-data-foundation
if ($LASTEXITCODE -ne 0) { throw "git switch failed" }
git pull --ff-only
if ($LASTEXITCODE -ne 0) { throw "git pull failed" }
& ".\Tests\Phase2OpenInterestIncremental\Run-Phase2Validation.ps1"
```

The runner builds the solution and executes all 16 offline test projects, with no configured PT writes or browser actions.
