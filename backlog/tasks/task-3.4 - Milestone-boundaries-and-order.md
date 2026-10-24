---
id: TASK-3.4
title: Milestone boundaries and order
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 16:52'
updated_date: '2026-10-24 17:05'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-3.2
references:
  - docs/adr/0018-memory-layout-requirements.md
  - docs/adr/0009-allocation-and-memory-budgets.md
parent_task_id: TASK-3
ordinal: 47000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

Given the test-surface research, what are the implementation milestones after M0, in what order, and which registry IDs, test files, hot-path budgets (ADR 0009) and seams does each own? Does ADR 0018's order (store and integrate, V1, V2, shared types, transactions and events, awareness and sync) hold, or must it be amended (for example merging integrate with the V1 decoder and a minimal transaction)?
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Milestones after M0 are named, ordered and each lists its owned registry prefixes, test files, budgets and seams
- [x] #2 Any change to ADR 0018's milestone list is recorded as an amendment to ADR 0018
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Grill milestone boundaries from docs/research/test-surface.md. 2. Record as an amendment to ADR 0018.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Confirmed in ADR 0006 CC-8 that every implicit transaction takes the doc lock, so transactions and the lock precede any mutator. Owner agreed Q1-Q9 in chat on 2026-10-24.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Milestones are M0 storage spike, M1 values and errors (alongside M0), M2 core (store, integrate, transactions with a simple lock, V1), then a parallel fan-out of M3 V2, M4 shared types and events, M6 protocols, with M5 doc features after M3 and M4, and M7 integration and release (sim, interop, full fuzz, CC stress, BUD-12, release gate). BUD-08 is owned by M3 and not split. The memory gate is none at M1, the spike harness at M2 and M3, and the slow tier from M4. Recorded as an amendment table in ADR 0018 with prefixes, test files, budgets and seams per milestone. Verified by owner review in chat.
<!-- SECTION:FINAL_SUMMARY:END -->
