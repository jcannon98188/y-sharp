---
id: TASK-4.38
title: 38. Simulation end to end
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 10:18'
labels:
  - spec
  - 'model:opus'
milestone: m-7
dependencies:
  - TASK-4.28
  - TASK-4.34
  - TASK-4.37
parent_task_id: TASK-4
ordinal: 38000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The sim harness has never run against an implementation. This absorbs TASK-2.2's open criterion and calibrates the gating budgets.

**Features:** `Sim`
**Owns:** `tests/YSharp.Tests/Simulation/`, `ISimInternals` adapter, `SimCase.GatingSeeds`
**Registry:** `SIM` entries
**Test files:** `Simulation/*`, `TestKit/SimHarnessTests.cs`
**Seams provided:** `ISimInternals` (over `HasPending` and `DumpStructs`)
**Handoff section-7 items:** End-to-end sim harness (`Init`, `Compare`, `ApplyRandomTests`) via `ISimInternals` (from TASK-2.2 AC #5); calibrate the 2-minute gating time budget and the ~60 s randomized budget via `SimCase.GatingSeeds` (with evidence, ADR 0022).
**Suggested model:** opus (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [x] #2 This spec's features are added to `Features.Landed`
- [x] #3 The traceability gate passes and no pending registry entry is added
- [x] #4 Port of the JS testHelper (TestConnector, init, compare) runs end to end with SIM-10+ entries covered
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
Result: the sim harness runs end to end through ISimInternals; Features.Landed gains Sim; no skip names Sim. Debug 4423 total, 0 failed (844 skipped); Release 4423, 0 failed (818 skipped); npm run check clean (141 fixtures).

Library fix found by the sim: Yjs cleanupYTextAfterTransaction walks the transaction delete set in its Map client order (first-insertion), not sorted. DeleteRanges now tracks that order on the transaction (trackClientOrder) and the formatting cleanup walks clients in it, so a format deleted before content turns the content's contextless cleanup into the full one (quill-medium, quill-large draw parity). New StructStoreLayoutTests case covers the order bookkeeping.

Calibration (ADR 0022 evidence): the randomized tier took 202 s in Release with 200/40/10 seeds; with 50/12/3 it takes about 58 s (1494 tests), inside the ~60 s budget. Recorded as an ADR 0017 amendment and in the spec-10 matrix.

Suite changes:
- SimHarness.Compare compares array/map toJSON with JsEquals (lib0 t.compare semantics: object keys by name, not order).
- FixtureReplay.AssertList compares elements before the count so a mismatch reports the first differing element.
- SimCases.GatingSeeds and SeedLists use 50/12/3 (insertdelete stays 10); RepeatedRandomTests self-test updated.
- ADR 0017 amendment and spec-10 traceability row for the seed counts.

Owner sign-off:
- (a) Sim map/array compare is now key-order-insensitive (matches lib0 compare; JS YMap.toJSON order differs between replicas too). Loosened assertion; stores, delete sets, state vectors and snapshots are still compared exactly.
- (b) Seed counts reduced to 50/12/3 to meet the ~60 s budget. Alternative: split the classes to run in parallel and keep 200/40/10.
- (c) Gap TASK-7: UndoManager walks stack-item delete sets sorted, JS walks them in Map client order; filed, not started.

Interop note: a stale differential failure (mutated update InsertByte at 24, V1) predated commit 7e11fbb; the oracle now reports parentNotAType and the rerun is green.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Sim harness (TestConnector, init, compare, applyRandomTests) runs end to end against y-sharp; Sim landed. Fixed formatting cleanup to walk deleted clients in Yjs Map order. Calibrated the randomized tier to ~58 s with 50/12/3 seeds (ADR 0017 amendment). Owner sign-offs: order-insensitive JSON compare, reduced seed counts, TASK-7 undo gap.
<!-- SECTION:FINAL_SUMMARY:END -->
