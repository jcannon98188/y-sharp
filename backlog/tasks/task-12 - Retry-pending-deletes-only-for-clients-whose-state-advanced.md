---
id: TASK-12
title: Retry pending deletes only for clients whose state advanced
status: To Do
assignee: []
created_date: '2026-10-26 23:57'
labels:
  - core
  - performance
dependencies:
  - TASK-11
ordinal: 68000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found in the performance audit after PR #2: Doc.ReadUpdate (src/YSharp/Integration/Doc.Integration.cs) re-applies the whole pending delete set (ApplyDeleteSet on InWriteOrder(_pendingDeletes)) on every update, so each update costs O(pending deletes) even when nothing it brings can resolve them. A pending range always starts at or past its client's state, so if that client's state has not advanced in this update, re-applying leaves the range exactly as it was. Re-apply only the ranges of clients whose state advanced, and keep the rest as they are. Yjs re-reads all of store.pendingDs each time; the resulting document, pending state and encodings are the same either way, so this is not visible to peers. The MaxPendingBytes dry run (Doc.PendingLimit.cs, Unapplied) mirrors this loop and must change in step. After TASK-11, which reworks the pending accounting in the same files.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 An update costs O(its own size) in pending-delete work when no pending range's client advanced
- [ ] #2 The pending delete set, transaction delete sets and every encoding stay identical to before on the existing pending, differential and fuzz tests
- [ ] #3 The MaxPendingBytes prediction stays exact
<!-- AC:END -->
