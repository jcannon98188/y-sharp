---
id: TASK-4.15
title: 15. Commit merge and garbage collection
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:41'
updated_date: '2026-10-25 16:49'
labels:
  - spec
  - 'model:sonnet'
milestone: m-2
dependencies:
  - TASK-4.14
parent_task_id: TASK-4
ordinal: 15000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Transaction cleanup merges adjacent structs and garbage-collects deleted content (ADR 0018 content rules).

**Features:** `Gc`
**Owns:** `src/YSharp/Integration/Gc/`, the GC hook implementation
**Registry:** `GC` entries and merge `STR` entries
**Test files:** `Structs/GarbageCollectionTests.cs`, `Structs/ContentTests.cs`
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

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Port the finally block of Yjs cleanupTransactions into HookGarbageCollection. 2. Make ContentType.gc iterative. 3. Land Gc, fix tests that assumed no commit merge (with oracle evidence). 4. Add V1-only GC tests on oracle bytes.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implementation (44ba5a6): src/YSharp/Integration/Gc/Doc.Gc.cs implements HookGarbageCollection: tryGcDeleteSet (Doc.Gc flag, DocOptions.GcFilter, keep), Item.gc with an explicit stack for nested types (children -> GC structs, branch cleared), tryMergeDeleteSet, merge of every client whose clock changed (from max(findIndex(before),1) right to left), merge of MergeStructs (then cleared). Additive Store internals (ADR 0021): Branch.ClearContent, Store/StructStore.Gc.cs (FindIndex, IdOfMergeStruct, CollectContent).

Verification: build 0 warnings; Debug 8728 total, 0 failed, 1119 passed; Release 0 failed, 1130 passed; npm run check clean; api surface unchanged.

Suite changes:
- With evidence (1f4de85): IntegrationEngineTests APartiallyKnownStruct, GcAndSkip, ADeleteSet_ArrivingBeforeItsStructs now expect the commit merge; AnItemIntoADeletedParent runs its uncollected case with GC off. Oracle (Yjs 13.6.33, fresh Doc, same updates): partial -> one ContentString (0,5); gc -> one GC (0,5); ds -> (0,1) live, (1,3) ContentDeleted, (4,1) live; gc-on parent -> child is a GC struct, gc-off -> deleted ContentAny item.
- Free (1a07cfb): new GcV1Tests (expected bytes from the pinned Yjs); V1RoundTripTests applies states with GC off, and the undo/capture-timeout-merging state (a redone item a fresh Yjs doc merges) moved to its own test with the fresh-Yjs bytes. The sim/map commit-merge cases (gated on Gc) now run and pass.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Transaction cleanup now garbage-collects deleted content (flag, filter, keep; nested types iteratively) and merges structs as Yjs cleanupTransactions does. Verified: 0 warnings, Debug and Release green, drift clean, oracle-byte GC tests and the full fixture round trip pass.
<!-- SECTION:FINAL_SUMMARY:END -->
