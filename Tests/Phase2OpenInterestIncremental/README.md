# Phase 2 incremental open-interest integration

This harness is synthetic/offline. The new OI collector and feature registry
are independent of the existing normalized OHLCV source and ProfitTrailer
configuration-delivery lifecycle.

15 case groups cover explicit opt-in, zero request budget, request/retention
bounds, 5m exact endpoints, bounded page size, pagination, fair request
scheduling, empty and missing provider periods, repeated-raid reuse, disk cache
restoration/corruption, no symbol mixing, quantity versus reported-value
change, Spot/US unavailable, and separate OI history requirements.

**Status:** authored; user-local build/test results pending by owner decision to
defer runtime acceptance. New feature code is not claimed as compiled.

In the PTMagic development repository:

\`\`\`powershell
git switch phase1/market-data-foundation
if ($LASTEXITCODE -ne 0) { throw "Branch switch failed" }
git pull --ff-only
if ($LASTEXITCODE -ne 0) { throw "Pull failed" }
git rev-parse HEAD
dotnet build .\PTMagic.sln
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
foreach ($project in @(
    ".\Tests\Phase2OpenInterest\Phase2OpenInterest.csproj",
    ".\Tests\Phase2OpenInterestIncremental\Phase2OpenInterestIncremental.csproj",
    ".\Tests\Phase1DCorrelation\Phase1DCorrelation.csproj",
    ".\Tests\TriggerConfigurationValidation\TriggerConfigurationValidation.csproj",
    ".\Tests\Phase1AnalyticsReliability\Phase1AnalyticsReliability.csproj"
)) {
    dotnet run --project $project
    if ($LASTEXITCODE -ne 0) { throw "FAILED: $project" }
}
\`\`\`

The configured runtime/API/Monitor/browser acceptance work is separately
deferred. Normal deployments with no OI metric configured produce no new OI
network requests, and the default OI request budget is 60 pages per raid.
