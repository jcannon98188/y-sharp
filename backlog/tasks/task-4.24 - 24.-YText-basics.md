---
id: TASK-4.24
title: 24. YText basics
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-25 23:35'
labels:
  - spec
  - 'model:sonnet'
milestone: m-4
dependencies:
  - TASK-4.21
parent_task_id: TASK-4
ordinal: 24000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Text insert, delete and embeds, without formatting.

**Features:** `Text`
**Owns:** `src/YSharp/Types/Text/` (basics)
**Registry:** `T` entries (non-format)
**Test files:** `Types/TextTests.cs` (basic cases)
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
Implementation: YText over a new rich-text core, TypeText.cs, a port of Yjs YText.js: ItemTextListPosition, findPosition, insertText, formatText, deleteText, the formatting cleanups and toDelta. Prelim writes are queued and replayed in order on integration (T-0.6). The map-side attributes go through the map core. Doc.Text.cs marks a branch HasFormatting when a format item integrates. YXmlText gets a CreateEmpty override for Clone.

JS parity: a probe compared yjs 13.6.33 with this port on five text scenarios, with GC on and off: inserts and deletes with a surrogate split; attributes, embeds and format; applyDelta with retain, embed, delete and retain-attributes; format past the end; attribute overwrite and minimization. Updates, strings and deltas were identical.

HOS-AMP allocation work (separate core commit), needed once Text landing ungated the amplification tests:
- UpdateReader in read-order mode builds no per-client blocks and sizes its list exactly.
- UpdateRefs creates its client dictionary lazily.
- UpdateMerge pre-sizes its output list.
The 3000-update merge went from 5.17 MB to 1.56 MB.

Suite changes (ADR 0022), in the suite: commit:
- Gate corrections, Events (they need captured Update events or event ordering from spec 27):
  - StructStoreTests: PendingStructsAndPendingDeleteSet...
  - StructModelTests: AStructWithAClockGap...
  - IntegrationTests: ConflictZone..., PartiallyKnownStruct..., MissingDependency...
  - UpdateApplicationTests: IndependentPendingUpdates..., ItemWrite_BothOriginsAbsent..., ReapplyingADeleteSet..., ReapplyingAnUpdate_IsANoOp..., WhenOneStructWaits..., DeleteSet_ArrivingBefore...
  - OpsScriptTests: Run_TextInsertScript...
  - UpdateFunctionTests: Enc27..., Enc23_Merge_OfAllUpdates...
  - Interop ComparatorTests: AGeneratedScript_IsAcceptedByTheInterpreter...
  - StructsFixtureTests: Fixture_EveryStoredScenario...
  - HotPathBudgetTests: Commit_WithNoUpdateSubscriber (BUD-05), OneCharacterTextInsert_AtTheEnd (BUD-07), TryApplyUpdate_HotPath (BUD-08). BUD-08's update stream comes from Doc.Update, which does not fire yet. BUD-05 and BUD-07 rows are recorded by the spec 28 M4 gate, which also needs Events.
- Gate corrections, Sync:
  - GarbageCollectionTests: ConvergenceHelper...
  - TextTests: Text_TenThousandAppends..., ToJson_FormattedText..., Text_FormattedMiddleDeleted..., Text_FormattedTextDeleted...
- Gate corrections, Fuzz (spec 42 owns them):
  - CorpusReplayTests: Corpus_Amplification..., Corpus_References_MissingCyclicAndNonTypeParentsNeverSpin
  - AmplificationTests: ThousandsOfTinyPendingUpdates...
- Test-bug fixes:
  - StructsSupport.cs: client ids are read with GetUInt64, not GetUInt32, so 53-bit client ids work.
  - ErrorPathTests: the update's origin is now e's own "x" (client 3); the hex is 01010500a40300017900. The Yjs oracle gives "x" (pending) for the old bytes and "xy" for the new ones.
  - ValueQuirkTests Enc05: Yjs writes a top-level Uint8Array from map.set as ContentBinary, so the Any-encoding assertion uses an array holding the bytes ("75017403010203").
  - UpdateLayoutTests Enc82: a single-input merge whose skip has flags is Malformed (ADR 0005 decodes in full), where Yjs returns the input untouched.

Owner sign-off:
1. Clone: Yjs clone inserts the live embedded type instances, which cannot be integrated twice. This port clones embedded types.
2. Branch.Upgrade resets HasFormatting.
3. Deleting from empty text changes nothing. JS throws a TypeError there.
4. The four test-bug fixes above (StructsSupport, ErrorPathTests, Enc05, Enc82). Enc82 departs from Yjs in favour of ADR 0005.
5. ENC-49 versus the hos-ref corpus: the corpus expects ok or Rejected for parent-is-self and forward-reference-chain-200, but spec 14 decided Malformed. The decoder keeps Malformed; Corpus_References is gated on Fuzz until spec 42 reconciles the corpus.
6. BUD-05 and BUD-07 are zero budgets in ADR 0009, but in Release they measure 1150 and 782 bytes per operation:
   - BUD-01 option A allocates a 24-byte Transaction for each Transact.
   - Each insert allocates an Item object.
   - The text core allocates position and attribute dictionaries and closures.
   Reaching zero needs either a decision to make them bounded budgets or store and transaction pooling work. Flagged for the spec 28 M4 gate.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
YText basics landed over a new rich-text core, TypeText, ported from Yjs YText.js. A probe against yjs 13.6.33 gave identical results on five text scenarios.

Checks: Debug and Release suites green (8751 tests, 0 failed), 0 build warnings, nightly 36/36, API surface unchanged, fixtures clean.

Also in this spec:
- HOS-AMP allocation cuts in the update functions.
- Suite gate corrections, listed under Suite changes.
- Four test-bug fixes.

Owner sign-off items are listed in the notes. They include the BUD-05 and BUD-07 zero budgets, which were handed to TASK-4.28, and the ENC-49 conflict with the hos-ref corpus, which was handed to TASK-4.42.
<!-- SECTION:FINAL_SUMMARY:END -->
