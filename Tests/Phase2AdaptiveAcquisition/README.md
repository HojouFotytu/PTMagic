# Phase 2 adaptive derivative acquisition — offline checks

Focused, network-free checks cover shared Binance Futures IP weight headers, the OI/global-account historical-data rolling allowance, funding's separate five-minute allowance, 429 and 418 cooldowns, family round-robin admission, deadline-bounded asynchronous waits for temporarily blocked quotas, independent family progress, cancellation during waits, freshness deferrals, and OI/funding/ratio progress beyond sixty requests without production raid caps.

The legacy bounded service overloads remain available to existing deterministic tests. Production passes an effectively unbounded request count and supplies shared adaptive page admission. A positive Binance delay no longer vetoes the entire family's raid if the limiter predicts recovery before the existing MaxAnalysisAgeSeconds cutoff. The scheduler waits without blocking other families, rechecks capacity after each wait, and defers only if the deadline cannot be met. Exact source coverage, persistent caches and UNAVAILABLE semantics remain in effect.

Run with the project-wide \`Tests/Phase2OpenInterestIncremental/Run-Phase2Validation.ps1\`, or:
\`\`\`powershell
dotnet run --project .\Tests\Phase2AdaptiveAcquisition\Phase2AdaptiveAcquisition.csproj
\`\`\`

These tests do not establish real Binance cooldown or configured raid acceptance.
