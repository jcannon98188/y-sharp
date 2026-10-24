---
id: TASK-3.3
title: 'Research: Yjs and Yrs struct layout and the baseline workloads'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 16:52'
updated_date: '2026-10-24 17:00'
labels:
  - 'wayfinder:research'
dependencies: []
references:
  - bench/yjs-memory
  - docs/adr/0018-memory-layout-requirements.md
  - docs/research/structs.md
parent_task_id: TASK-3
ordinal: 46000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

What do the three slow-tier workloads actually create in Yjs v13.6.33 (items, merged items, content objects, search markers), so that "bytes per item" in `bench/yjs-memory/yjs-bytes-per-item.json` (text 194.77, array 8.31, map 312.26) is understood per op or per struct? How do Yjs and Yrs lay out structs, content, the struct store and the delete set, and what does Yrs do with arenas or indexes? What must a minimal spike store implement to run those workloads honestly (merge on insert, search marker or equivalent for position lookup)?

Output: `docs/research/storage-layout.md`.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 `docs/research/storage-layout.md` explains the unit behind each Yjs bytes-per-item figure
- [x] #2 It describes the Yjs and Yrs struct and content layout with source references
- [x] #3 It lists the minimum operations a spike store needs to run the three workloads fairly
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Written to `docs/research/storage-layout.md`. Yjs figures are retained heap per operation, not per struct: text = 1M one-char Items (~192 B each), array = one Item holding one 8 MB JS array (8.31 B/op, itself breaking the ADR 0018 chunking rule), map = 100k Items (~312 B per key). Yjs uses search markers; Yrs walks linearly and boxes 160-byte Items. The array ceiling needs a homogeneous 8-byte chunked column. ADR 0009 defines neither "item" nor the 100k steady-state edit mix; the spike must pin both. No .NET or Yrs runtime measurements yet.
<!-- SECTION:FINAL_SUMMARY:END -->
