---
id: TASK-4.22
title: 22. YArray
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-25 22:55'
labels:
  - spec
  - 'model:sonnet'
milestone: m-4
dependencies:
  - TASK-4.21
parent_task_id: TASK-4
ordinal: 22000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Array type per types.md and ADR 0008.

**Features:** `Array`
**Owns:** `src/YSharp/Types/Array/`
**Registry:** `A` entries
**Test files:** `Types/ArrayTests.cs`
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
1. TypeList: JS typeListDelete partial behaviour (delete what exists, then throw), typeListPushGenerics via Branch.End (ADR 0023), slice with Array.prototype.slice clamping.
2. YArray over TypeList: implicit read/write transactions (CC-8); prelim buffer spliced like JS _prelimContent and inserted at 0 from OnIntegrated; ToArray/Slice/CopyTo/indexer; ListEnumerator live inside an explicit transaction, snapshot outside (CC-9); Clone and ToJson as Yjs.
3. Land Feature.Array; apply gate corrections in a suite: commit.
4. Verify convergence ahead of the Sync-gated tests with a throwaway multi-doc probe; fix what it finds.
5. Full checks, notes, finalize.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implementation (2026-10-25):
- Prelim YArray: Insert/Push/Unshift/Delete splice a List<YEntry> as JS splices _prelimContent; reads see nothing (T-0.4); OnIntegrated inserts the buffer at 0 inside the integrating transaction, nested prelims follow recursively (T-0.6, T-0.8). Prelim Delete never throws past the end (JS splice); negative index/length throw (A-20).
- Push inserts after Branch.End with no marker update (nothing after the end moves). Delete past the end deletes what exists, updates markers, then throws "Length exceeded!" (A-5); y-sharp updates markers before throwing where JS leaves them stale (no wire effect).
- Bug found and fixed: search markers were never cleared after a remote change, so an index read after ApplyUpdate could return the wrong element (a throwaway 3-doc random-ops probe diverged from a naive model on 5/5 seeds). Doc.Transactions.CleanupOne now clears the markers of every changed type of a non-local transaction before the hooks (Yjs AbstractType._callObserver clears them there). An ApplyUpdate nested in Transact is covered too: IntegrateUpdate already forces IsLocal = false, as Yjs readUpdateV2. Added a regression test that fails without the fix.
- Probe also checked: prelim two-level nesting replays, Clone is a detached prelim copy, live enumerator throws YLifetimeException after its transaction, single-doc 300-op random sequences match a naive model (8 seeds). Probe deleted.

Gate corrections (standing approval):
- ArrayTests: 13 TestConnector-based tests -> + Sync (TestConnector calls YSync.WriteStep1).
- SharedTypeTests.Set_ReturnsTheLiveInstance_Array (port of testYmapSetsYarray) -> + Map, Sync.
- StepKindTests.ArrayInsertType_NestedType_IsReachableByPath, StructModelTests.GcStruct_IsWrittenFromAnOffset..., UpstreamPortTests.Decode_OldV1ArrayDocument_YieldsTheStoredArray -> + Map (YMap.Set / YMap.ToJson).
- UpstreamPortTests.MergeUpdates_SequentialArrayInserts / MergeUpdates_InsertThenDelete -> + Events (record from Doc.Update, raised by spec 27).
- UpstreamPortTests.MergeUpdates_ConcurrentArrayInserts_EqualsApplyingBothUpdates -> + Sync.
- CanaryTests 1M nested arrays, fuzzed depths, 1M nested maps -> + DocLifecycle (the canary calls Doc.Destroy).

Suite changes (ADR 0022):
- c97a5c5 suite: gate corrections for TASK-4.22 (above).
- 2d58579 suite: ArrayTests.SearchMarkers_AfterARemoteChange_AreDiscarded_EvenWhenAppliedInsideALocalTransaction (A-14), new regression test.

Owner sign-off (non-blocking, JS-faithful default not taken): inserting into a prelim YArray at an index past its buffered count throws ArgumentOutOfRangeException("Length exceeded!"), matching an integrated array (A-2). JS splices _prelimContent, which silently clamps to the end. Chosen for consistency between prelim and integrated arrays; no wire effect. Flip if you want the JS clamp.

Validation: build 0 warnings; Debug and Release 8751 tests, 0 failed; no skip reason names Array; memory gate 36/36; API surface unchanged (733 lines); fixtures up to date (141 files).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
YArray implemented over the list core (TypeList): prelim buffering with replay at integration, JS-faithful push/delete/slice, live-or-snapshot enumeration (CC-9), Clone and ToJson. Found and fixed stale search markers after remote changes (cleared at cleanup of non-local transactions, as Yjs _callObserver), with a regression test. Feature.Array landed; gate corrections in a suite: commit. Verified: Debug and Release 8751 tests green, no skip names Array, memory gate 36/36, API surface and fixtures unchanged. One non-blocking owner sign-off item (prelim insert past the end throws instead of clamping).
<!-- SECTION:FINAL_SUMMARY:END -->
