---
id: TASK-3.4
title: Milestone boundaries and order
status: To Do
assignee: []
created_date: '2026-10-24 16:52'
updated_date: '2026-10-24 17:00'
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
- [ ] #1 Milestones after M0 are named, ordered and each lists its owned registry prefixes, test files, budgets and seams
- [ ] #2 Any change to ADR 0018's milestone list is recorded as an amendment to ADR 0018
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
From the research: doc features (UndoManager, snapshots, relative positions, subdocs, PUD, CollectGarbage) and the YValue/StateVector layer belong to no ADR 0018 milestone; transactions must come no later than the first mutator; V1 apply is needed by almost every STR test. See docs/research/test-surface.md sections 3-5.
<!-- SECTION:NOTES:END -->
