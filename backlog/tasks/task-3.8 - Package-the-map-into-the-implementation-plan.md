---
id: TASK-3.8
title: Package the map into the implementation plan
status: To Do
assignee: []
created_date: '2026-10-24 16:52'
updated_date: '2026-10-24 17:47'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-3.1
  - TASK-3.4
  - TASK-3.5
  - TASK-3.6
  - TASK-3.7
  - TASK-3.9
references:
  - docs/handoff-implementation-planning.md
parent_task_id: TASK-3
ordinal: 51000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

Turn the resolved map into the plan under TASK-4: Backlog milestones, session-sized spec tasks with acceptance criteria, dependencies and the per-spec definition of done; schedule the handoff section-7 items; close TASK-2.2 and TASK-2 with a pointer to the plan task that absorbs the sim-harness criterion.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 TASK-4 has Backlog milestones and session-sized spec tasks with acceptance criteria and dependencies
- [ ] #2 Every handoff section-7 item is scheduled in a plan task
- [ ] #3 TASK-2.2 and TASK-2 are closed with a pointer to the absorbing plan task
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
From TASK-3.9 (ADR 0020): the plan must define the feature list (one feature per spec, each milestone a set of features). Its first spec, "Feature gates", goes in M0 and blocks M1.

From TASK-3.6 (ADR 0021): M0 also holds a "Source layout skeleton" spec (split the stub into module folders and partial files, declare the hook points, no behaviour change, API-diff check), which blocks M1. Every spec names the folders and files it owns.

From TASK-3.5 (ADR 0018 spike amendment): the M0 spike is a spec (or two: candidates plus harness) whose output is the storage ADR. The M2 store spec adds the third driver adapter and deletes spike/ once the harness passes.
<!-- SECTION:NOTES:END -->
