# Funding-only adaptive acquisition checks

## Current metric/acquisition scope — November 19, 2026

The application still registers **43 metrics (30 globally capable / 13 asset-only)**; this harness's latest documented **9/9 PASS** is source-specific evidence from the earlier Phase 3 catalogue checkpoint, not a new validation of the later `4bbe299e` no-trade/coverage change or any newly proposed metric. The current [trading-need-first development policy](../../CHANGELOG-2.10.md#november-19-2026--trading-need-first-metric-reassessment-documentation-only-design-authority) requires a demonstrated LONG/SHORT trading need before adding acquisition work.

For any candidate, first determine the necessary timeframe, source granularity, retention, refresh cadence, market coverage and freshness *from the measurement*, then assess source reliability, Binance request weight/provider quotas and cumulative load from **both** PTMagic instances. The diagnostic **96-hour** store is not an acquisition constraint or a reason to reject longer history. Conversely, available cached inputs do not justify an unnecessary metric. Preserve current pinned-raid age protection, rate-limit safety, missing-source validity and cold/warm/gap-recovery verification.

The previously removed open-interest and trader-positioning families remain out of scope. No new endpoint, metric, acceptance result or acquisition algorithm is introduced by this documentation-only change.

## Current 43-metric acquisition verification — November 17, 2026

**User-run `Phase2AdaptiveAcquisition` 9/9 PASS** at the Phase 3 checkpoint following `33a33f0`: **43 canonical names, 30 globally capable, 13 asset-only**. Funding provider quota, shared IP weight, Futures 5m pagination/request-weight invariants, 429/418 cooldowns, recoverable waits, cancellation, freshness/capacity deadlines and absence of the former sixty-page ceiling all PASS. This harness no longer validates removed open-interest/positioning families, and offline checks do not prove configured runtime or PT delivery. Older 26-metric/8-check evidence below is historical.




D-053 removes the open-interest and positioning services. The existing `Phase2AdaptiveAcquisition` harness now tests the retained funding path: the 26-metric catalogue (17 global-capable, nine asset-only), funding quota, Binance IP-weight headers, 429/418 cooldowns, deadline-bounded admission, recoverable waits, cancellation and absence of a fixed 60-page funding ceiling.

Run `dotnet run --project .\Tests\Phase2AdaptiveAcquisition\Phase2AdaptiveAcquisition.csproj`. Do not use retired OI/positioning validation scripts as an active acceptance requirement. Offline tests do not prove live provider/Monitor/PT behavior.

**Verification boundary:** All eight current groups **passed at `f7d802facfabb4611098d695d88a269d60daa278`**, including the 26-metric registry check (17 globally evaluable, nine asset-only). The earlier 8/8 run at `e31635a1` was the pre-taker baseline. The removed OI/positioning groups are obsolete. [November 12 source audit](https://github.com/HojouFotytu/PTM_2.10_Roadmaps_Handoffs/blob/main/updates/2026-11-12-d053-removal-code-audit.md).

**Focused verification — November 13, 2026 (KST):** User-run at PTMagic source [`f7d802facfabb4611098d695d88a269d60daa278`](https://github.com/HojouFotytu/PTMagic/commit/f7d802facfabb4611098d695d88a269d60daa278). `dotnet build .\PTMagic.sln` **PASS in 16.1 seconds** (Core, PTMagic, Monitor net10.0). `Phase1COhlcv` **15/15**, `NormalizedWarmStartCache` **10/10**, `Phase2AdaptiveAcquisition` **8/8** — **33/33 focused test groups PASS**. This establishes source-specific compilation and offline checks, **not** configured live Binance/TestMode metric values, PT settings delivery, or Monitor browser acceptance.
