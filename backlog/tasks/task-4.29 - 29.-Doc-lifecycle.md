---
id: TASK-4.29
title: 29. Doc lifecycle
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 07:39'
labels:
  - spec
  - 'model:sonnet'
milestone: m-5
dependencies:
  - TASK-4.28
parent_task_id: TASK-4
ordinal: 29000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Doc options, load and destroy, lifecycle events and the doc seams the subdoc and GC tests reach.

**Features:** `DocLifecycle`
**Owns:** `src/YSharp/DocFeatures/Lifecycle/`, `Doc.Lifecycle.cs`
**Registry:** `DOC` entries
**Test files:** `DocFeatures/DocTests.cs`
**Seams provided:** `ShouldLoad`, `AutoLoad`, `CollectionId`, `Meta`, `CollectGarbage`, `SignalLoaded`, `SignalSynced` (TestKit reaches these via `DocSeams` reflection)
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
1. Implement Guid (option or uuid v4), the load flags, Meta and CollectionId from the options.
2. SignalLoaded and SignalSynced, following the Yjs load and sync handlers.
3. Destroy as a write-locked operation: mark destroyed, run the subdoc hook, raise Destroying then Destroyed, drop the handlers.
4. CollectGarbage as Yjs tryGc over createDeleteSetFromStructStore.
5. Land DocLifecycle, then fix or gate the tests it activates, and run the gates.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implemented in src/YSharp/DocFeatures/Lifecycle/Doc.Lifecycle.cs:
- Guid is the option, else a lazily drawn uuid v4.
- ShouldLoad is the option, or true once Load ran. AutoLoad, CollectionId and Meta come from the options.
- SignalLoaded and SignalSynced follow the Yjs 'load' and 'sync' constructor handlers. Loaded fires before Synced, as in JS.
- Destroy takes the write lock without opening a transaction (CC-17), so no transaction events fire, matching Yjs. It sets IsDestroyed, runs the OnDestroySubdocs hook (spec 33), and raises Destroying then Destroyed (JS emits 'destroyed' then 'destroy'). It then clears the doc's event handlers, as lib0 ObservableV2.destroy does. Type observers stay, and the doc keeps accepting transactions.
- CollectGarbage is Yjs tryGc(createDeleteSetFromStructStore): it collects whatever Gc says, then merges, under the write lock with no transaction or events.
- Handler faults go to ObserverFaulted (ADR 0005).
- New tests: CollectGarbage bytes checked against Yjs 13.6.33 (GC-04), and Destroy dropping handlers while keeping type observers (DOC-15).

Suite changes (ADR 0022): e31bc60 fixes two DocTests that first ran when DocLifecycle landed, both checked against Yjs.
- TypedGetters called GetArray('') after GetText(''), which Yjs rejects.
- The nested-transaction order expected empty origins where Yjs gives 'first' and 'second'.

Owner sign-off:
- Destroy clears all doc-level handlers except ObserverFaulted, which is y-sharp's own. This matches JS, where a destroyed doc emits no update or afterTransaction events. ADR 0006 CC-17 says only IsDestroyed changes; read that as about lock acquisition.
- BeforeAllTransactions passes the first transaction, while Yjs passes only the doc. This is the existing ADR 0008 handler shape.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
DocLifecycle landed. Doc has a guid (option or uuid v4), ShouldLoad, AutoLoad, CollectionId and Meta, and the load and sync signals with the Yjs flag semantics. Destroy is write-locked and opens no transaction; it raises Destroying then Destroyed and drops the doc handlers as ObservableV2 does. CollectGarbage is Yjs tryGc over the whole store. Subdoc behaviour is hooked for spec 33. Two DocTests that Yjs contradicts were fixed in a suite commit. Debug and Release are green with 0 warnings, npm check is clean, and no test that needs DocLifecycle skips.
<!-- SECTION:FINAL_SUMMARY:END -->
