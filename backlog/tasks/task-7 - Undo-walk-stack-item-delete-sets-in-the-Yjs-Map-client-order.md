---
id: TASK-7
title: 'Undo: walk stack-item delete sets in the Yjs Map client order'
status: To Do
assignee: []
created_date: '2026-10-26 10:03'
labels:
  - gap
  - undo
dependencies: []
ordinal: 62000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Yjs DeleteSet.clients is a Map, so iterateDeletedStructs visits clients in the order each first got a range. mergeDeleteSets and sortAndMergeDeleteSet keep that order. UndoManager.popStackItem builds itemsToRedo from stackItem.deletions in that order, then redoes them in order, so when one stack item deleted items of several clients, the order decides which redone item gets the lower new clock. y-sharp's UndoManager.IterateDeletedStructs (src/YSharp/DocFeatures/Undo/UndoManager.cs) walks clients in ascending id order, and its Copy and Merge helpers drop the order. Its comment already notes the difference. TASK-4.38 added DeleteRanges(trackClientOrder: true) with ClientOrder and FirstIndexOf, and the transaction delete set now tracks the order (the YText formatting cleanup needed it to match quill-medium). Carry that order into the stack items (Copy keeps it; Merge appends the second set's new clients, as mergeDeleteSets does) and walk it in IterateDeletedStructs. Then add an oracle fixture (an undo of a deletion of items by two clients, the higher id deleted first) to show y-sharp and JS give the same redo clocks.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Stack item deletion sets keep the Yjs client order through Copy and Merge
- [ ] #2 IterateDeletedStructs walks clients in that order
- [ ] #3 An oracle fixture (an undo that redoes items of two clients deleted higher id first) replays byte-identically
<!-- AC:END -->
