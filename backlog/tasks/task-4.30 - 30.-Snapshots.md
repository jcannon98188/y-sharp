---
id: TASK-4.30
title: 30. Snapshots
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 07:57'
labels:
  - spec
  - 'model:sonnet'
milestone: m-5
dependencies:
  - TASK-4.29
parent_task_id: TASK-4
ordinal: 30000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
`YSnapshot` create, encode, decode, `CreateDoc`, `ContainsUpdate`.

**Features:** `Snapshots`
**Owns:** `src/YSharp/DocFeatures/Snapshots/`
**Registry:** `SNAP` entries
**Test files:** `DocFeatures/SnapshotTests.cs`, snapshot cases of `Structs/GarbageCollectionTests.cs`
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
Implementation (b923291):
- YSnapshot keeps the state vector plus each client's delete ranges exactly as created or decoded (clients in first-appearance order, ranges in read order). Equals, Encode and ContainsUpdate use those raw ranges, as Yjs equalSnapshots, encodeSnapshotV2 and snapshotContainsUpdate do (SNAP-03). DeleteSet, IsVisible and the attribute reads use them sorted and merged.
- TryDecode reads the format's delete-set decoder (DSDecoderV1 or V2), then a state vector. A client listed twice gets its later ranges appended; zero-length ranges are kept, as JS keeps them. Truncation and values above 2^53-1 are Malformed; trailing bytes are ignored.
- CreateDoc runs inside an origin write transaction (origin null): it splits at each state vector clock, writes structs 0..clock as one V2 update plus the raw delete set, then applies the update to the target with origin "snapshot".
- YText.ToDelta(snapshot, previous, computeYChange) is a port of Yjs toDelta. It opens a "cleanup" write transaction, or joins the caller's. SplitAffectedStructs runs once per snapshot per transaction (TxState.SplitSnapshots, cleared at End). The ychange merge test compares cur.user and cur.type the way JS does, so the default {type} starts a new op at every item. A new test, ToDelta_YChangeWithAUserField..., pins this against the pinned Yjs.
- YXmlElement.GetAttributes(snapshot) ports typeMapGetAllSnapshot (TypeMap.Entries(type, snapshot)). A value collected after the snapshot reads as undefined, as JS getContent()[len-1] does on ContentDeleted.

Suite changes:
- 322b449 suite: gate corrections. TextTests testSnapshot and testSnapshotDeleteAfter, and SnapshotTests testDependentChanges, build their documents through the sim harness (YSync), so they also require Feature.Sync.

Owner sign-off:
- Decoded snapshots: visibility and DeleteSet.Contains use the merged, sorted ranges. JS binary-searches the raw list, which can miss a range when a hostile snapshot holds unsorted or overlapping ranges.
- ContainsUpdate decodes and validates the whole update first, so a malformed update throws YDecodeException even where JS would return false at an earlier struct beyond the state vector. Zero-length delete ranges in the update are dropped on read (ENC-51), so they never turn the answer false the way an unmergeable zero-length DeleteItem can in JS.
- CreateDoc throws InvalidOperationException before writing anything when the snapshot's clock for a client is past the origin's state. JS fails mid-transaction with a TypeError or unexpectedCase. CreateDoc also writes client blocks in descending client order where JS uses map insertion order; the restored content is the same.
- YSnapshot overrides Equals(object) and GetHashCode with content equality (state vector hash), so the HashSet of split snapshots compares by reference explicitly, as the JS Set in transaction.meta does.
- A snapshot ToDelta inside ReadTransact throws YConcurrencyException (CC-3, no lock upgrade). JS has no read transactions. This is documented on ToDelta.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
YSnapshot landed: Create, Empty, Encode and TryDecode (V1 and V2), raw-range Equals, ContainsUpdate, and CreateDoc, plus the snapshot reads YText.ToDelta(snapshot, previous, computeYChange) and YXmlElement.GetAttributes(snapshot). Feature.Snapshots is in Landed. Three harness-based ports were gated on Sync. Debug and Release suites are green with 0 warnings, and the fixtures are up to date. Five owner sign-offs are in the notes: decoded-range visibility, ContainsUpdate strictness, CreateDoc's early throw and block order, content equality, and the CC-3 throw.
<!-- SECTION:FINAL_SUMMARY:END -->
