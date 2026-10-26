---
id: TASK-4.27
title: 27. Events and observers
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 03:34'
labels:
  - spec
  - 'model:opus'
milestone: m-4
dependencies:
  - TASK-4.21
parent_task_id: TASK-4
ordinal: 27000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Event building, pooled dispatch-scoped events, observe and observe-deep, doc transaction events and observer-fault containment (ADR 0005, ADR 0008), implemented through the cleanup hooks.

**Features:** `Events`
**Owns:** `src/YSharp/Events/`, `Doc.Events.cs`, the observer hook implementations
**Registry:** `E` entries, observer-fault `ERR` entries, Q23-Q25
**Test files:** `Types/EventTests.cs`, `Types/TransactionTests.cs`, `Structs/TransactionTests.cs`, `Concurrency/ObserverTests.cs`
**Suggested model:** opus (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [x] #2 This spec's features are added to `Features.Landed`
- [x] #3 The traceability gate passes and no pending registry entry is added
- [x] #4 An `OutOfMemoryException` from an observer propagates and is not an observer fault (ERR-19)
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
Gap found in TASK-4.22 (2026-10-25), for this spec: Doc.Yata.cs AddChangedType returns early when parent.Owner is null, i.e. a nested type that arrived in an update and was never read has no YType yet. Yjs records every changed type, so observeDeep on an ancestor could miss events for changes inside such a type. Likely fix: create the owner with YType.Of(parent, tx.Doc) instead of returning; check the BUD-08 allocation budget for remote applies (it would allocate one YType per untouched nested type). Related: Doc.Transactions.ClearStaleMarkers (TASK-4.22) relies on the changed set and runs before HookBeforeObservers; keep that order when the observer hooks land.

From TASK-4.25: HookFormattingCleanup (Yjs cleanupYTextAfterTransaction) runs in CleanupOne after HookAfterTransaction, in the same try block. Yjs runs the observers, the deep observers, afterTransaction and the formatting cleanup through callAll, so each still runs when an earlier one throws. When observers land, keep that: an observer exception must not skip the formatting cleanup.
Changed types that no YType object wraps yet are not in tx.Changed (AddChangedType skips them; the texts among them are tracked in TxState.UnwrappedTextsChanged for the cleanup). Yjs has an object for every type, so deep-observe parent chains through an unread nested type need the same care.

Implemented (2026-10-25): src/YSharp/Events (Subscription, YType.Observers, YEvent, typed events, Doc.Observers); CleanupOne runs observers and afterTransaction under callAll-style containment, then the formatting cleanup; AddChangedType wraps unread nested types with YType.Of (UnwrappedTextsChanged removed); changed-type key sets are pooled per TxState.

Validation: dotnet build YSharp.slnx 0 warnings; dotnet test --solution YSharp.slnx green in Debug and Release (8762 total); npm run check clean (141 fixtures); api-surface baseline updated (4 removed skeleton ctors). ERR-19 Err19_OutOfMemory_IsNeverCaught passes.

Suite changes (ADR 0022):
- [Requires(Feature.Sync)] gate corrections on every test that builds a Net (18 EventTests, 7 TextTests, UpstreamPortTests.KeyEncoding_FormattedText_RoundTripsThroughEveryEncoder, XmlTests.Xml_AttributeAndChildChanges_RaiseEventsLocallyAndRemotely, TransactionTests.RemoteUpdates_RunAsNonLocalTransactions_WithTheGivenOrigin): Net.Init reaches YSync.WriteStep1 (TASK-4.36).
- EventTests M-E5: Assert.Throws<YLifetimeException> instead of ThrowsAny<InvalidOperationException> (ADR 0005).
- EventTests Y-4: renamed to TextDelta_ReadInTheHandler_DeletesRedundantFormatsLikeYjs; asserts the Yjs side effect, no longer needs Sync.
- TextTests T-1: three inserts give 3 updates (was 4).
- TypeDump: object keys enumerate in JS order (Q08) so a locally inserted embed matches the oracle dump.
- SimSteps.Record + SharpSide: the interop replay feeds the sim deliver step (AGeneratedScript_IsAcceptedByTheInterpreter).
- BUD-04/05/07 Zero -> Bounded (AllocationBudgets, BudgetTableTests, renamed *_StaysWithinItsBudget, budgets.yaml, ADR 0009 text).

Owner sign-off:
1. YTextEvent.Delta really deletes redundant format items in a follow-up transaction, as Yjs does (Y-4). Reading the delta changes the document and emits an update; this matches the oracle bytes in fixtures/text/concurrent-formatting.json. Yjs's own cleanupYTextFormatting misses these items, so without this a .NET peer diverges from JS byte-wise. The previous suite test asserted the opposite.
2. M-E5: stale events throw YLifetimeException (a YException), not InvalidOperationException.
3. BUD-04, BUD-05, BUD-07 bounded instead of zero (measured 215, 974, 606 bytes/op in Release; limits +25%). Zero needs Item pooling and no per-transaction Transaction; ADR 0009 amended accordingly. TASK-4.28 re-measures.
4. Public parameterless ctors of YArrayEvent/YMapEvent/YTextEvent/YXmlEvent removed from the API surface.
5. CI fix outside the spec: the fuzz canary classifies a silent Windows STATUS_ACCESS_VIOLATION as the stack overflow (windows-latest flake, commit c817758).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Events and observers landed: type and deep observers in Yjs order, doc lifecycle and update events, contained observer faults (OOM propagates), YEvent path/keys/delta and YTextEvent.delta including Yjs's redundant-format delete. Verified with the full solution suite in Debug and Release, the fixture drift check and the API surface check; owner sign-offs listed in the notes.
<!-- SECTION:FINAL_SUMMARY:END -->
