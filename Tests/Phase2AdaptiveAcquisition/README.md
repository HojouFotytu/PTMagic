# Funding-only adaptive acquisition checks

D-053 removes the open-interest and positioning services. The existing `Phase2AdaptiveAcquisition` harness now tests the retained funding path: the 26-metric catalogue (17 global-capable, nine asset-only), funding quota, Binance IP-weight headers, 429/418 cooldowns, deadline-bounded admission, recoverable waits, cancellation and absence of a fixed 60-page funding ceiling.

Run `Tests/Phase2OpenInterestIncremental/Run-Phase2Validation.ps1` or `dotnet run --project .\Tests\Phase2AdaptiveAcquisition\Phase2AdaptiveAcquisition.csproj`. Offline tests do not prove live provider/Monitor/PT behavior.

**Verification boundary:** All eight current groups **passed at `f7d802facfabb4611098d695d88a269d60daa278`**, including the 26-metric registry check (17 globally evaluable, nine asset-only). The earlier 8/8 run at `e31635a1` was the pre-taker baseline. The removed OI/positioning groups are obsolete. [November 12 source audit](https://github.com/HojouFotytu/PTM_2.10_Roadmaps_Handoffs/blob/main/updates/2026-11-12-d053-removal-code-audit.md).

**Focused verification — November 13, 2026 (KST):** User-run at PTMagic source [`f7d802facfabb4611098d695d88a269d60daa278`](https://github.com/HojouFotytu/PTMagic/commit/f7d802facfabb4611098d695d88a269d60daa278). `dotnet build .\PTMagic.sln` **PASS in 16.1 seconds** (Core, PTMagic, Monitor net10.0). `Phase1COhlcv` **15/15**, `NormalizedWarmStartCache` **10/10**, `Phase2AdaptiveAcquisition` **8/8** — **33/33 focused test groups PASS**. This establishes source-specific compilation and offline checks, **not** configured live Binance/TestMode metric values, PT settings delivery, or Monitor browser acceptance.
