# PTMagic 2.10 — Market Analysis Expansion

**Single entry point for the PTMagic 2.10 development roadmap, implementation status, decisions, revisions and handoffs.**

- Product version: **2.10** (2.9 modernization release is finished).
- Application repository: [HojouFotytu/PTMagic](https://github.com/HojouFotytu/PTMagic).
- Active development branch: `phase1/market-data-foundation`.
- Last source-code HEAD verified before these documentation commits: `9e00a1f25068ed23c3d64b708e0cc85b5a7eaef4` (2026-11-04). This is a dated reference, not an assertion about current branch HEAD.
- Framework: .NET 10; normalized analytics target: Binance Futures USDT perpetuals.

## Canonical technical plan and historical sources

| Document | Role |
|---|---|
| [Revised PTMagic 2.10 roadmap](roadmaps/revised-2.10-2026-11-04.txt) | **Authoritative technical roadmap**; detailed requirements, phase definitions and 2026-11-04 observations |
| [Original roadmap](roadmaps/original.txt) | Immutable historical baseline for comparison |
| [Previous 2.9.1-labeled revision](roadmaps/revised-2.9.1-2026-11-04.txt) | Historical revision retained for provenance; **not the current project/version designation** |
| [Phase 0 architecture audit](audits/phase-0-architecture-audit.txt) | Completed baseline architecture findings |
| [Current status](STATUS.md) | **Operational source of truth for tracked tasks and blockers**, updated as work proceeds |
| [Change log](CHANGELOG.md) | Chronological record of roadmap/documentation revisions |
| [Decisions](DECISIONS.md) | Accepted decisions and explicit open design choices |
| [Dated updates](updates/README.md) | Evidence-oriented implementation and roadmap update notes |
| [Handoffs](handoffs/README.md) | Dated session handoffs, starting with Phase 1C |
| [Known issue: cold-start readiness](issues/001-cold-start-readiness.md) | First blocking correctness investigation |

## Precedence

1. **Actual repository source and verified runtime results** decide what is implemented and working.
2. **Accepted dated decisions** govern intent and supersede incompatible historical wording.
3. **The revised 2.10 roadmap** governs feature scope and technical constraints.
4. **STATUS.md** records the latest *evidence-backed* implementation status, without silently rewriting the underlying roadmap.
5. Earlier revisions, handoffs and audits remain historical records. Never silently overwrite history.

A task marked [DONE] must identify the relevant commit and test evidence when available. Unverified claims remain [PARTIAL], [ISSUE] or [PLANNED].

## Update protocol

1. Read the actual code-branch HEAD and relevant code before any development claim.
2. Refer to stable task IDs in [STATUS.md](STATUS.md).
3. Update the status, issue and decision records affected by a change.
4. Add a dated entry under [updates/](updates/) using [the template](updates/TEMPLATE.md), citing commits, evidence, limitations and next steps.
5. When a new handoff is supplied, archive it as a dated file under [handoffs/](handoffs/) and update its index. Do **not** rewrite an earlier handoff in place.
6. When the roadmap itself changes materially, update the authoritative roadmap **with an explicit review** and document what changed and why in CHANGELOG.md; retain historical copies in Git history.
7. Never label proposed fixes as verified. Never treat successful download or successful build as proof of analytics correctness.
8. Avoid application code changes in documentation-only commits. Do not merge or alter release branches without explicit authorization.

## Current gate

**Phase 1A cold-start metric completeness must be investigated and corrected or explicitly contained before Phase 1C feature implementation.** Initial 4h asset metrics: **259/1,434 valid**, subsequently **1,434/1,434**. Root cause not verified. Preserve existing legacy behavior and fail-closed normalized triggers.

**Next after gate:** Phase 1C-1 ATRP (per asset), Phase 1C-2 median market ATRP, then volatility breadth, quote-volume expansion/breadth and volume concentration.
