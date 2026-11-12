# Funding-only adaptive acquisition checks

D-053 removes the open-interest and positioning services. The existing `Phase2AdaptiveAcquisition` harness now tests the retained funding path: the 24-metric catalogue, funding quota, Binance IP-weight headers, 429/418 cooldowns, deadline-bounded admission, recoverable waits, cancellation and absence of a fixed 60-page funding ceiling.

Run `Tests/Phase2OpenInterestIncremental/Run-Phase2Validation.ps1` or `dotnet run --project .\Tests\Phase2AdaptiveAcquisition\Phase2AdaptiveAcquisition.csproj`. Offline tests do not prove live provider/Monitor/PT behavior.
