---
id: TASK-7
title: 'Undo: walk stack-item delete sets in the Yjs Map client order'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-26 10:03'
updated_date: '2026-10-27 00:07'
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
- [x] #1 Stack item deletion sets keep the Yjs client order through Copy and Merge
- [x] #2 IterateDeletedStructs walks clients in that order
- [x] #3 An oracle fixture (an undo that redoes items of two clients deleted higher id first) replays byte-identically
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Stack item delete sets (deletions and insertions) now always track the client order. Copy keeps it; Merge appends the second set's new clients, as mergeDeleteSets does. IterateDeletedStructs walks that order using FirstIndexOf.

Beyond the ticket: insertions are built from the transaction's store slots (TxState.AddInsertions) instead of the descending StateVector. Slot order is the order each client first got a struct, which is the JS store.clients / afterState Map order. This matters because popStackItem deletes the insertions in reverse iteration order, and that delete order becomes the next stack item's deletions order.

Verified: the new fixture fails on the old code (the struct store differs, with B and A swapped) and passes with the change.

Suite changes:
- tools/fixture-gen/scenarios/docfeatures.mjs: new undo scenario redo-order-follows-delete-order (clients 2 and 9; one transaction deletes 9's item and then 2's; then undo, redo and undo).
- fixtures/undo/redo-order-follows-delete-order.json and fixtures/index.json regenerated.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Undo stack items now keep the Yjs client order of their delete sets through Copy and Merge, and IterateDeletedStructs walks that order, so redone items get the same clocks as in Yjs. A new oracle fixture (an undo that redoes items of clients 9 and 2, deleted 9 first) replays byte for byte; it failed before the change.
<!-- SECTION:FINAL_SUMMARY:END -->
