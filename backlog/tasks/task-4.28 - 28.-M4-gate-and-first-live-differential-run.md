---
id: TASK-4.28
title: 28. M4 gate and first live differential run
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 07:27'
labels:
  - spec
  - 'model:sonnet'
milestone: m-4
dependencies:
  - TASK-4.20
  - TASK-4.22
  - TASK-4.23
  - TASK-4.25
  - TASK-4.26
  - TASK-4.27
parent_task_id: TASK-4
ordinal: 28000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Close M4 by the ADR 0018 gate, with the slow memory tier now runnable, and run the live Node differential for the first time while divergences are still cheap.

**Owns:** `AllocationBudgets.Limits` rows for M4
**Budgets:** BUD-01, 02, 04, 05, 07, 09 (bounded measured plus 25%, zero paths read 0); BUD-13 and BUD-14 slow tier green
**Handoff section-7 items:** First live Node-versus-y-sharp runs (text, array and map generators); any divergence handled under ADR 0022.
**Suggested model:** sonnet (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every listed budget recorded and active; slow memory tier green
- [x] #2 `TypeFixtureTests` and `WireFixtureTests` pass
- [x] #3 Live differential smoke and nightly text/array/map runs pass, or each mismatch is fixed or raised as an owner decision
- [x] #4 Every M4 spec is Done and the M4 milestone is closed
- [x] #5 The traceability gate passes and no pending registry entry is added
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
1. Record the remaining M4 budget rows (BUD-09 Push/Set) from Release measurements plus 25%, and re-check BUD-01/02/04/05/07.
2. Run the slow memory tier (BUD-13, BUD-14) under YSHARP_TIER=Nightly in Release.
3. Run the live differential, Smoke then Nightly, with Node; fix each mismatch or register a recorded divergence with a normaliser (ADR 0012, 0022).
4. Run the fixture tests, the traceability gate, Debug and Release suites, and npm check.
5. Notes, suite commits, mark Done (closes M4).
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
From TASK-4.24: HotPathBudgetTests BUD-05 and BUD-07 are gated on Events, so this gate activates them. Release measurements at TASK-4.24: BUD-05 1150 bytes per operation (OrderedDictionary x9, closures x3, Position x2, HashSet, Transaction); BUD-07 782 bytes (Item x3, Int32[] x3, OrderedDictionary x2). ADR 0009 makes both zero, but BUD-01 option A allocates a Transaction for each Transact and every insert allocates an Item, so zero needs an owner decision (bounded budgets) or pooling work. This is listed for owner sign-off in TASK-4.24.

Budgets (Release, M4 gate): BUD-09 recorded as Push 128 and Set 200 bytes per call, plus 25% (ac82709). BUD-01, 02, 04, 05 and 07 were re-checked and are within budget. MemoryTierTests (BUD-13, BUD-14) pass under YSHARP_TIER=Nightly. The only skips are the three sync and awareness hot paths, which wait on later specs.

Live differential: Smoke, then Nightly with YSHARP_REQUIRE_NODE=1. The runs found the following:
- Bug fixed: a merged pending state kept parentSub for items with an origin; Yjs drops it, writing 0x88 where y-sharp wrote 0xa8 (0fbc801).
- Bug fixed: the integration took the origin from left after the right-origin lookup. That lookup can split the origin's struct, which rewrote the origin to an earlier clock. The origin is now taken as left.lastId first, as Yjs getMissing does (0fc0d52). Covered by the STR-14 test.
- ENC-64: Yjs reads a default past the end of a shortened V2 column, and y-sharp rejects it (ADR 0005). Claimed by LenientColumnEndNormaliser, using the oracle flag readPastEnd.
- ENC-51: Yjs rejects a zero-length delete range for an unknown client ("Unexpected case" from writeDsLen), and y-sharp ignores it. Claimed by ZeroLengthDeleteRangeNormaliser (zeroLengthDeleteRange).
- New divergence ENC-86 (ADR 0003): Yjs parks out-of-order delete ranges and re-encodes them as V2 with a negative clock difference. That corrupts the pending delete set and every later state encode. y-sharp keeps the ranges as sent. Claimed by UnsortedPendingDeleteSetNormaliser (unsortedPendingDeleteSet), with sv and doc still compared. Also covered by a fixture case and a [Diverges] unit test.
- Q15 (ADR 0005): Yjs parks for good a struct whose origin, right origin or parent is its own id or a later clock of its client, and accepts the update. y-sharp rejects it as Malformed (TASK-4.14). Claimed by SelfDependentStructNormaliser (selfDependentPending), only when y-sharp is unchanged.
- ContentDoc: mutated updates that reach TypeList.ContentAt before Subdocs land are discarded (DiffRun.ReachesUnlandedSubdocs). TASK-4.33 has a note to remove this.
- The oracle misbehaved on some cases, which are saved by design and are not failures: many "TypeError: Cannot read properties of undefined (reading 'id')", two "Error: Unexpected case" on FlipByte at 11, and one Node timeout on DeleteByte at 35.

Suite changes (ADR 0022): 118a9d6 bounded BUD-04/05/07; fcedefd LenientColumnEndNormaliser; ac82709 BUD-09 rows; 5f9da12 ZeroLengthDeleteRangeNormaliser; 66d8580 pending-state encode expectation; 48b923f ENC-86 normaliser and divergence; 3a52e15 sv/doc comparison on ENC-86 claims and the unlanded-subdocs discard; 860b7df origin test; a3bcbfd SelfDependentStructNormaliser.

Owner sign-off:
- Bounded budgets for BUD-04, BUD-05 and BUD-07 instead of the zero required by ADR 0009 (also recorded in TASK-4.24).
- The new ENC-86 divergence: y-sharp keeps out-of-order pending delete ranges instead of reproducing the Yjs corruption.
- Q15 strictness for self-dependent structs: y-sharp rejects where Yjs parks the struct for good. This favours correctness; such a struct can never integrate in either implementation.
- Extending the ENC-64 and ENC-51 normalisers to the live differential.
- The temporary discard of ContentDoc cases until Subdocs land.
- Accepting the oracle-misbehaved saves as Yjs crashes rather than mismatches.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed M4. BUD-09 recorded (Push 128, Set 200 bytes plus 25%); BUD-01/02/04/05/07 re-checked; slow memory tier BUD-13/14 green in Release. First live Node differential: Smoke and Nightly green (Nightly 100 passed, 1 skipped). It found two y-sharp bugs, both fixed: parentSub kept in a merged pending state, and an origin rewritten after a right-origin split. It also found one new divergence (ENC-86) and three known ones (ENC-64, ENC-51, Q15), each claimed by a flag-gated normaliser. ContentDoc cases are discarded until Subdocs land (TASK-4.33 note). Debug and Release suites green (8776, 0 failed), 0 warnings, npm check clean, traceability gate green. Owner sign-offs are listed in the notes.
<!-- SECTION:FINAL_SUMMARY:END -->
