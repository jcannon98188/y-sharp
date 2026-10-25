---
id: TASK-4.25
title: 25. YText formatting and delta
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-25 23:49'
labels:
  - spec
  - 'model:opus'
milestone: m-4
dependencies:
  - TASK-4.24
parent_task_id: TASK-4
ordinal: 25000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Formatting attributes, `ApplyDelta`/`ToDelta`, attribute equality and `CleanupFormatting`: the subtlest part of the text type.

**Features:** `TextFormat`
**Owns:** `src/YSharp/Types/Text/Formatting/`
**Registry:** `F`, `D` entries, Q27
**Test files:** `Types/TextTests.cs` (format and delta cases)
**Seams provided:** `YText.CleanupFormatting()`
**Suggested model:** opus (override freely)

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
Implementation:
- Most formatting (format, applyDelta, toDelta, attribute equality, CleanupFormatting) came with the TASK-4.24 text core (TypeText.cs).
- This spec adds Yjs cleanupYTextAfterTransaction (Types/Text/Formatting/Doc.FormattingCleanup.cs) through a new HookFormattingCleanup. It runs after HookAfterTransaction. When a non-local transaction changes a text that has held a format item, a follow-up local transaction:
  - fully cleans each text that got a new format item or lost one;
  - removes the contextless format gaps left around deleted content.
- The walk over new structs ends at before+after, matching Yjs passing afterClock as the length.
- Changed texts that no YType object wraps yet (a relay that never reads them) are tracked in TxState.UnwrappedTextsChanged, since Yjs has an object for every type.

JS parity: a probe against yjs 13.6.33 was identical, with GC on and off, on:
- overlapping and identical concurrent formats;
- delete versus format;
- nested texts relayed by a doc that never reads them, in both orders.
With the hook disabled, the identical-format and relay cases diverge, so the probe exercises the cleanup.

Suite changes (ADR 0022):
- Gate corrections, Sync (the convergence helper uses YSync, spec 36): TextTests Text_SplitSurrogatePair_SurvivesDisconnectAndSync, ToDelta_ApplyDeltaEmbed_ReturnsTheEmbed, ToDelta_EmbedWithAttributes_IncludesTheAttributes, ToDelta_EmbedWithoutAttributes_OmitsTheAttributesKey, AttributeEquality_IsShallow_SoFlatObjectsAreEqualAndNestedOnesAreNot.
- Test-bug fix: StepKindTests TextApplyDelta_InsertRetainDeleteAndAttributes_AreApplied wrote its delta ops as plain JSON objects. The ops script format, which the fixtures and the interpreter share, tags every object as {"$":"obj","pairs":...}, so the ops are now tagged.
- New tests (F-8): RemoteChange_SameConcurrentFormat_DeletesTheDuplicateFormatItems and RemoteChange_ToANestedTextNobodyRead_StillCleansUpItsFormatting. Both assert the oracle's state bytes.

Owner sign-off:
1. The StepKindTests script fix above.
2. The cleanup runs in the same try block as the after-transaction hook. Yjs callAll runs it even when an observer throws; that is recorded for TASK-4.27.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
TextFormat landed. The formatting code came with the TASK-4.24 text core; this spec adds Yjs cleanupYTextAfterTransaction, which deletes format items that a remote change made redundant. It also covers nested texts that no shared type object wraps yet.

A JS probe was identical to Yjs, and with the hook disabled it diverges, so the probe really exercises the cleanup. Two oracle regression tests were added.

Checks: Debug and Release green (8753 tests, 0 failed), 0 warnings, nightly 36/36, API surface unchanged, fixtures clean.
<!-- SECTION:FINAL_SUMMARY:END -->
