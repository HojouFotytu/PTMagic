# Phase 2 — USD-M open-interest raw foundation

This source-only first batch adds typed native open-interest observations and
an explicit optional Binance USD-M current/history client. **It does not wire
new exchange requests into ordinary PTMagic raids, register analyzer metrics,
change configured triggers, or change PT/Monitor behavior.**

Official endpoint contracts verified November 10, 2026:
- \`GET https://fapi.binance.com/fapi/v1/openInterest?symbol=BTCUSDT\`
- \`GET https://fapi.binance.com/futures/data/openInterestHist?symbol=BTCUSDT&period=5m&endTime=<pinned UTC ms>&limit=<1..500>\`
- Binance states historical statistics cover approximately the latest month,
  maximum 500 rows per query, and omit periods without trades. The provider
  separately reports \`sumOpenInterest\` and \`sumOpenInterestValue\`; native units
  are retained rather than silently converted or summed across assets.
- Open-interest history rows use their provider \`timestamp\` (period end);
  current \`openInterest\` uses a different \`time\` and is not a history bucket.
- All ordinary HTTP calls use the existing BaseAnalyzer transport, with its
  Binance rate-limiter, retry and cancellation logic.

This batch maps provider fields, rejects malformed/mixed/ambiguous data, keeps
only source observations no newer than the request pin, and computes a
2-endpoint percentage change only at exact completed 5m UTC endpoints.
Missing periods, missing reference values and unsupported horizons remain
unavailable; no interpolation, currency conversion or arbitrary price factor.

One historical page is supported per explicit caller request. Before wiring the
feature to MarketTrend.Metrics, design opt-in selection, incremental cache,
pagination, requests-per-raid budget, the 30-day source horizon, unavailable
semantics for Spot/BinanceUS, and per-metric export/Monitor integration. Do not
fan out one historical HTTP request per symbol every raid.

## Deterministic local validation

Run on the PTMagic application development branch:

\`\`\`powershell
git switch phase1/market-data-foundation
if ($LASTEXITCODE -ne 0) { throw "Branch switch failed" }
git pull --ff-only
if ($LASTEXITCODE -ne 0) { throw "Git pull failed" }
git rev-parse HEAD
dotnet build .\PTMagic.sln
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
dotnet run --project .\Tests\Phase2OpenInterest\Phase2OpenInterest.csproj
if ($LASTEXITCODE -ne 0) { throw "Phase 2 OI harness failed" }
\`\`\`

Expected case inventory: 18 offline groups; user-local results pending. No
live exchange, PT API, or Monitor access is required for these tests.
