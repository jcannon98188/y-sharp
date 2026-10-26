---
id: TASK-4.31
title: 31. Relative positions
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 08:08'
labels:
  - spec
  - 'model:sonnet'
milestone: m-5
dependencies:
  - TASK-4.29
parent_task_id: TASK-4
ordinal: 31000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Relative and absolute positions, encode, decode and JSON.

**Features:** `Positions`
**Owns:** `src/YSharp/DocFeatures/Positions/`
**Registry:** `RP` entries
**Test files:** `DocFeatures/RelativePositionTests.cs`
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
Implementation: RelativePosition Encode/TryDecode/ToJson/TryFromJson/Resolve and YType.CreateRelativePosition, ports of utils/RelativePosition.js. followRedone goes through StructStore.RedoneOf(ItemRef), which returns null until spec 32 records item.redone.

Suite changes:
- 15a7ceb gate corrections: RelativePosition_DeleteThenUndo_FollowsTheRestoredContent and FollowUndoneDeletions_MayDifferBetweenReplicas_SoComparisonsUseFalse gained Feature.Undo (they drive an UndoManager).
- d7af097 DocFeatureFixtureTests replays with CodecSteps.CreateRegistry: relpos fixtures use array.insertType, which StandardSteps does not register. Checked by ungating locally: every relpos/snapshot/doc fixture replays except relpos/follow-undone-deletions (needs Undo).

Owner sign-off:
- tname anchors for a root the doc does not have resolve to null; JS doc.get(tname) creates a placeholder root (ADR 0008, RP-06).
- TryDecode rejects (Malformed) an unknown tag, where JS yields an all-null position that throws later; also clocks past 2^53-1 and an assoc outside int. Trailing bytes are ignored, as in JS.
- TryFromJson is strict: members of the wrong shape are Malformed (JS accepts anything). {"assoc":0} gives an all-null position whose Encode and Resolve throw, as in JS.
- CreateRelativePosition on an unintegrated type throws InvalidOperationException (JS throws a TypeError from findRootTypeKey).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Relative positions land: creation from a type index, binary encode/decode, JSON, and resolution to absolute positions with followRedone through a store hook for spec 32. Feature.Positions is landed; two Undo-driven tests are gated on Feature.Undo and the doc-feature fixture replay now uses the codec step registry. Debug and Release suites green, fixtures up to date. Sign-offs: tname null resolve, strict decode/JSON, throw on unintegrated types.
<!-- SECTION:FINAL_SUMMARY:END -->
