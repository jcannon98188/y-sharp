---
id: TASK-4.33
title: 33. Subdocs
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 09:09'
labels:
  - spec
  - 'model:sonnet'
milestone: m-5
dependencies:
  - TASK-4.29
parent_task_id: TASK-4
ordinal: 33000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Subdocument tracking, load and the `SubdocsChanged` event, through the subdocs hook.

**Features:** `Subdocs`
**Owns:** `src/YSharp/DocFeatures/Subdocs/`, `Doc.Subdocs.cs`
**Registry:** `SUB` entries
**Test files:** `DocFeatures/SubdocTests.cs`
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
From TASK-4.28: DiffRun.ReachesUnlandedSubdocs discards mutated-update differential cases that reach the ContentDoc sketch (TypeList.ContentAt) while Subdocs is not landed. When this spec lands Subdocs, delete that check and UnlandedSubdocsDiscardTests' else branch, and rerun the Nightly differential.

From TASK-4.29: Doc.Lifecycle.cs declares partial void OnLoadSubdoc() (Load on an unloaded subdoc: add it to the parent transaction's subdocsLoaded) and partial void OnDestroySubdocs() (destroy the subdocs, then detach this subdoc and put an unloaded copy in its ContentDoc). Implement both in Doc.Subdocs.cs. Destroy drops every doc handler except SubdocsChanged, which nothing raises yet (CS0414). Add SubdocsChanged = null there when this spec raises it.

From TASK-4.32: StructStore.CopyContent (used by the undo manager's redoItem) throws Sketch.NI for ContentDoc; spec 33 must copy a subdocument the way Yjs ContentDoc.copy does (new Doc with the same guid and options, shouldLoad false).

Implementation (commit 061abc5): SubdocContent holds guid and normalized options; FromWire follows Yjs createDocFromOpts. Integrate and delete record into the transaction's SubdocChanges; HookSubdocs runs after the update events, sets client and collection ids, updates doc.Subdocs, raises SubdocsChanged and destroys removed docs. Load on an unloaded subdoc opens a parent transaction to report it loaded. Destroy releases the doc's lock, destroys its subdocs, then (DetachFromParent) puts a fresh unloaded doc with the same guid into the item and reports it added unless the item is deleted. StructStore CopyContent copies ContentDoc (undo of a subdoc delete). YXmlFragment.InsertSubdoc implemented.

Suite changes (commit 4ad17fc):
- SubdocTests.AddAndDelete_InOneTransaction_CancelOut: expects Yjs's event with loaded:[gone] only.
- TypeDump: subdoc entries dump as {kind:subdoc, guid}, as observe.mjs.
- DiffRun.ReachesUnlandedSubdocs deleted; UnlandedSubdocsDiscardTests replaced by MutatedContentDocTests (the TASK-4.28 Nightly case now applies without a fault).

Verification: Debug and Release suites green; npm run check up to date; DocFeatureFixtureTests replayed locally with only UserData ungated: every subdoc fixture passes (only pud/* fail, spec 34); no skip reason names Subdocs.

Owner sign-off:
- Decoded ContentDoc options are normalized (gc/autoLoad/shouldLoad by JS truthiness, meta kept unless undefined), so re-encoded bytes can differ from a non-canonical input, matching Yjs, which re-encodes from its opts object.
- A string guid or collectionid inside the wire options overrides, as in Yjs createDocFromOpts.
- Subdocs inherit the parent's Limits (no JS counterpart).
- Destroy takes the document lock in two separate steps (mark destroyed, then raise Destroying/Destroyed) so the parent transaction runs with the subdoc's lock released (CC-18).
- YArray.ToJson of a subdoc entry is the subdoc's ToJson() (Yjs: Doc.toJSON).
- The Debug lock-order checker is deferred to TASK-4.39 (noted there).

Nightly differential (Debug, YSHARP_TIER=Nightly, Node required): 99 passed, 1 skipped, 1 failed. The failure was Differential_MutatedValidUpdates_FallIntoAKnownOutcomeClass: an inserted byte gave a struct whose parent id names a ContentAny item. Yjs integrates it as a GC; y-sharp rejects the struct. That is the recorded STR-71 divergence (ADR 0005), not a subdoc issue, but it had no normaliser. Suite change (commit 7e11fbb): steps-foreign.mjs flags parentNotAType; ParentNotATypeNormaliser (STR-71) claims it; ApplyForeign_AStructWhoseParentIsNotAType_IsFlagged pins the flag. npm check is clean. The targeted oracle tests pass; the 25-minute Nightly was not re-run.
Owner sign-off: STR-71 keeps rejecting a struct whose parent is not a type. Following JS (integrate it as a GC) would be the interop-first choice; the reversal is small (Doc.Yata.cs Resolve, default branch), if wanted.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Subdocs landed: ContentDoc integrate and delete, the subdocs event, load and destroy, with fixtures matching the oracle. Debug and Release suites are green and npm check is clean. The Nightly differential found one unclaimed STR-71 case; it is now covered by a new normaliser (suite commit). Owner sign-offs are listed in the notes.
<!-- SECTION:FINAL_SUMMARY:END -->
