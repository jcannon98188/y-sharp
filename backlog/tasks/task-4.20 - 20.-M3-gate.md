---
id: TASK-4.20
title: 20. M3 gate
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:41'
updated_date: '2026-10-25 19:11'
labels:
  - spec
  - 'model:haiku'
milestone: m-3
dependencies:
  - TASK-4.19
parent_task_id: TASK-4
ordinal: 20000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Close M3 by the ADR 0018 milestone gate.

**Owns:** `AllocationBudgets.Limits` rows for BUD-08
**Budgets:** BUD-08 (`TryApplyUpdate` V1 and V2, bounded): measured plus 25%, entry active
**Suggested model:** haiku (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The spike harness memory gate is green
- [x] #2 BUD-08 is recorded for both formats and active
- [x] #3 Every M3 spec is Done and the M3 milestone is closed
- [x] #4 The traceability gate passes and no pending registry entry is added
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 `dotnet build YSharp.slnx` has 0 warnings
- [x] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [x] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [x] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Measure BUD-08 for V1 and V2 in Release. HotPathBudgetTests needs Text, so add a codec-level test (as BUD-06 at M2) that builds the one-character insert stream by hand, V2 via TryConvert.
2. Keep the hot path within budget: if spec 19's pending dry run shows up, add an allocation-free fast path.
3. Record WithHeadroom(measured) for both parts, mark BUD-08 active, ratchet the pending mark.
4. Memory gate (Nightly Release run of RealStoreDriverTests and SpikeHarnessTests), full suites, drift, API surface; close M3.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
BUD-08: first measurement 2181 bytes per call (V1 and V2), against 1068 for V1 at the M2 gate: spec 19's MaxPendingBytes dry run ran on every apply. Added NothingCanWait (2ae83bf): with nothing pending, contiguous client runs from their state, cross-client dependencies already stored and delete ranges below state, the prediction is exactly 0 without allocating. The Debug assertion that compares each prediction with the pending state left passes across the Debug suite. Remeasured 1069 bytes per call for V1 and V2; recorded WithHeadroom(1069) = 1337 for BUD-08:V1 and BUD-08:V2. The theory now runs and passes in Release.

Memory gate: YSHARP_TIER=Nightly Release run of RealStoreDriverTests and SpikeHarnessTests: 36/36 passed.

Verification: dotnet build 0 warnings; Debug 1197 passed / 0 failed; Release 1211 passed / 0 failed; npm run check clean (141 fixtures, plan goldens); api-surface unchanged (733 lines). M3 specs 4.18 and 4.19 are Done.

Suite changes (ADR 0022, 2209688, with evidence): new CodecBudgetTests theory TryApplyUpdate_OfAOneCharacterInsertStream_StaysWithinItsBudget (BUD-08, V1 and V2); AllocationBudgets.Limits rows BUD-08:V1 and BUD-08:V2; BUD-08 pending -> active in docs/inventory/budgets.yaml; registry pending high-water mark 10 -> 9. No open owner decision.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
M3 closed. BUD-08 is recorded and active for V1 and V2 at 1337 bytes (1069 measured plus 25%), measured by a new codec-level test that runs before Text lands. To stay within it, the MaxPendingBytes dry run now has an exact, allocation-free fast path for the common case; the Debug prediction check still covers it. Verified with the Nightly memory gate (36/36), Debug and Release suites (0 failed), fixture drift and API surface checks.
<!-- SECTION:FINAL_SUMMARY:END -->
