---
id: TASK-1.4
title: 'Research: doc-level features inventory'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 19:55'
updated_date: '2026-10-23 19:59'
labels:
  - 'wayfinder:research'
dependencies: []
parent_task_id: TASK-1
ordinal: 5000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

Inventory Doc, subdocs, UndoManager, Snapshot, RelativePosition, PermanentUserData, GC on/off. Output: docs/research/doc-features.md.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Inventory of Doc, subdocs, UndoManager, Snapshot, RelativePosition, PermanentUserData and GC on/off written as ~80 numbered behaviours (DOC-/SUB-/UM-/SNAP-/RP-/PUD-/GC- IDs) with yjs file:line refs, existing oracle test pointers, edge cases and candidate divergences (RP-05, PUD-05/07, SNAP-04). Flags deterministic seams needed for RNG, clock and scheduler. File: docs/research/doc-features.md
<!-- SECTION:FINAL_SUMMARY:END -->
