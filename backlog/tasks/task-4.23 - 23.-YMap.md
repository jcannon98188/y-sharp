---
id: TASK-4.23
title: 23. YMap
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-25 23:02'
labels:
  - spec
  - 'model:sonnet'
milestone: m-4
dependencies:
  - TASK-4.21
parent_task_id: TASK-4
ordinal: 23000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Map type per types.md and ADR 0008.

**Features:** `Map`
**Owns:** `src/YSharp/Types/Map/`
**Registry:** `M` entries, Q26
**Test files:** `Types/MapTests.cs`
**Suggested model:** sonnet (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [x] #2 This spec's features are added to `Features.Landed`
- [x] #3 The traceability gate passes and no pending registry entry is added
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
Implementation: YMap (src/YSharp/Types/Map/YMap.cs) over TypeMap; prelim entries in an OrderedDictionary (JS Map order) set in order on integration; Count/Keys/ToJson/enumerator are snapshots under a read transaction; the enumerator throws YLifetimeException once the explicit transaction it was taken in ends (CC-9). TypeMap.Delete now returns whether a live entry was deleted (YMap.Delete returns it; the write transaction always runs, as in JS).

Verification beyond the suite (Map ports are Sync-gated): a 3-replica probe (clients 5, 2, 9; concurrent sets of one key, delete, undefined value, nested map, clear, a late set) gives byte-identical EncodeStateAsUpdate to pinned Yjs 13.6.33, with gc on and off.

Suite changes (ADR 0022), commit b35360a:
- Gate corrections (+Sync): 12 Net-based MapTests, SharedTypeTests.Set_ReturnsTheLiveInstance_Map.
- Gate corrections (+Events, Net.Capture): UpdateApplicationTests.ItemWrite_AMapEntryWritesItsKeyOnlyWithNullOrigins_AnOverwriteInheritsIt, StructModelTests.GcStruct_IsWrittenFromAnOffset_AndIntegratedWithOne_AndNeverWaitsForAnything.
- Gate correction (+Text): StepKindTests.ArrayInsertType_NestedType_IsReachableByPath.
- Gate correction (+DocLifecycle): ArrayTests.Array_IteratingNestedTypes_VisitsEveryElementInOrder.
- Test correction: GarbageCollectionTests.ChildrenOfACollectedType_AreDiscardedWhenGcIsOn_AndKeptHiddenWhenItIsOff used Net.Both(a, b), which sends a's delete to b first; b (gc on) then collects its own late set and sends a GC, so a gets a GC whatever its gc setting. Pinned Yjs gives the same (GC for both settings in that order; GC / deleted Item when b sends first). The test now syncs b to a first, then a to b, so a's gc setting decides as its comment says.

Owner sign-off: the GC-07 test correction above changes a test's steps, not just its gate (YSharp matched Yjs; the test's sync order did not match its own intent).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
YMap landed over the map core with JS prelim ordering, CC-8 implicit transactions and CC-9 enumerators; TypeMap.Delete reports a live deletion. Feature.Map landed; 18 tests gate-corrected or corrected (see Suite changes), one test-step correction awaiting owner sign-off. Debug and Release suites (8751) green, nightly allocation (36), API surface (733 lines unchanged) and fixtures (141) checks pass; a 3-replica map probe is byte-identical to pinned Yjs with gc on and off.
<!-- SECTION:FINAL_SUMMARY:END -->
