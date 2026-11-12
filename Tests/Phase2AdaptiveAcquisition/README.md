# Funding-only adaptive acquisition checks

D-053 removes the open-interest and positioning services. The existing `Phase2AdaptiveAcquisition` harness now tests the retained funding path: the 26-metric catalogue (17 global-capable, nine asset-only), funding quota, Binance IP-weight headers, 429/418 cooldowns, deadline-bounded admission, recoverable waits, cancellation and absence of a fixed 60-page funding ceiling.

Run `Tests/Phase2OpenInterestIncremental/Run-Phase2Validation.ps1` or `dotnet run --project .\Tests\Phase2AdaptiveAcquisition\Phase2AdaptiveAcquisition.csproj`. Offline tests do not prove live provider/Monitor/PT behavior.

**Verification boundary:** Eight current groups are authored. User ran 8/8 PASS at `e31635a1` before taker flow; the new 26-metric registry check is **not yet executed** at the current Phase 2 source. The removed OI/positioning groups are obsolete. [November 12 source audit](https://github.com/HojouFotytu/PTM_2.10_Roadmaps_Handoffs/blob/main/updates/2026-11-12-d053-removal-code-audit.md).
