# Phase 2 adaptive derivative acquisition — offline checks

Focused, network-free checks cover shared Binance Futures IP weight headers, the OI/global-account historical-data rolling allowance, funding's separate five-minute allowance, 429 and 418 cooldowns, family round-robin admission, freshness/cancellation deferrals, and OI/funding/ratio progress beyond sixty requests without production raid caps.

The legacy bounded service overloads remain available to existing deterministic tests. Production passes an effectively unbounded request count and supplies shared adaptive page admission, using the raid's existing MaxAnalysisAgeSeconds cutoff. Exact source coverage, persistent caches and UNAVAILABLE semantics remain in effect.

Run with the project-wide \`Tests/Phase2OpenInterestIncremental/Run-Phase2Validation.ps1\`, or:
\`\`\`powershell
dotnet run --project .\Tests\Phase2AdaptiveAcquisition\Phase2AdaptiveAcquisition.csproj
\`\`\`

These tests do not establish real Binance cooldown or configured raid acceptance.
