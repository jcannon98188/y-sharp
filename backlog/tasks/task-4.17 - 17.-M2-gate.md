---
id: TASK-4.17
title: 17. M2 gate
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:41'
updated_date: '2026-10-25 16:59'
labels:
  - spec
  - 'model:haiku'
milestone: m-2
dependencies:
  - TASK-4.15
  - TASK-4.16
parent_task_id: TASK-4
ordinal: 17000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Close M2 by the ADR 0018 milestone gate.

**Owns:** `AllocationBudgets.Limits` rows for M2, `docs/inventory/budgets.yaml` statuses
**Budgets:** BUD-06 (`EncodeStateAsUpdate` into a pre-sized writer, zero); record the V1 figure for BUD-08 in the notes (entry stays pending until spec 20)
**Suggested model:** haiku (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The spike harness memory gate is green on the real store
- [x] #2 BUD-06 reads 0 and is active; the BUD-08 V1 measurement is in the notes
- [x] #3 Every M2 spec is Done and the M2 milestone is closed
- [x] #4 The traceability gate passes and no pending registry entry is added
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 `dotnet build YSharp.slnx` has 0 warnings
- [x] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [x] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [x] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Memory gate: YSHARP_TIER=Nightly Release run of RealStoreDriverTests and SpikeHarnessTests: 36/36 passed (spike gates and figures on the real store).

BUD-06: EncodeStateAsUpdate into a pre-sized writer measured 0 bytes per call (Release, CodecBudgetTests: 200 chars text, 20 array values, 20 map entries from one V1 update). To reach 0: read transactions the library opens itself no longer allocate their Transaction facade (TxState.BeginRead/Facade, TxHandle holds TxState, DocLock readers hold TxState); Lib0Writer.Rent reuses a per-thread writer object (buffers still rented/returned). Cross-module internal change to TASK-4.12's Transactions files; no observable behaviour change; BUD-01/02 tests unchanged and green. HotPathBudgetTests' own BUD-06 test runs once Text, Array and Map land.

BUD-08 (V1, stays pending until spec 20): 1068 bytes per TryApplyUpdate, measured in Release on a stream of one-character text inserts arriving one update at a time (warmup 50, 200 measured).

Verification: 0 warnings; Debug 8748 total, 0 failed, 1141 passed; Release 0 failed, 1153 passed; drift clean; api surface unchanged.

Suite changes (with evidence): BUD-06 pending -> active in docs/inventory/budgets.yaml; registry pending high-water mark 13 -> 10 (current pending count); new CodecBudgetTests (BUD-06).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
M2 closed: the real-store memory gate is green, BUD-06 reads 0 and is active, the BUD-08 V1 figure (1068 B/apply) is recorded, and every M2 spec (11-17) is Done. Verified: 0 warnings, Debug and Release green, drift clean.
<!-- SECTION:FINAL_SUMMARY:END -->
