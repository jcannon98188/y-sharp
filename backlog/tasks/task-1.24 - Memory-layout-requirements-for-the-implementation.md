---
id: TASK-1.24
title: Memory layout requirements for the implementation
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 21:31'
updated_date: '2026-10-23 21:51'
labels:
  - 'wayfinder:grilling'
dependencies: []
references:
  - docs/adr/0009-allocation-and-memory-budgets.md
  - docs/adr/0004-pure-csharp-implementation.md
parent_task_id: TASK-1
ordinal: 25000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
ADR 0004 and 0009 make GC pressure first-class, but the implementing agents need explicit layout requirements (structs, arenas, pooling, chunked storage avoiding LOH growth) and how the budgets gate each step.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Layout requirements stated without prescribing code
- [x] #2 How ADR 0009 budgets gate implementation milestones is defined
- [x] #3 Recorded as an ADR
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Grilled with the user: layout requirements are measurable outcomes plus hard invariants, with the object-vs-arena choice left to the implementer behind an M0 spike gate; content is chunked shared slices; transients pooled and bounded; budgets gate per milestone with ratcheting. Recorded in docs/adr/0018-memory-layout-requirements.md (reviewed with the user in chat; no code to test).
<!-- SECTION:FINAL_SUMMARY:END -->
