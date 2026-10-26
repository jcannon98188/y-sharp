---
id: TASK-3.8
title: Package the map into the implementation plan
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 16:52'
updated_date: '2026-10-24 19:43'
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
- [x] #1 TASK-4 has Backlog milestones and session-sized spec tasks with acceptance criteria and dependencies
- [x] #2 Every handoff section-7 item is scheduled in a plan task
- [x] #3 TASK-2.2 and TASK-2 are closed with a pointer to the absorbing plan task
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Size specs from docs/research/test-surface.md. 2. Grill the breakdown, gate specs, differential scope, template, TASK-2 closure and assignment. 3. Create milestones and specs under TASK-4, wire dependencies. 4. Close TASK-2.2 and TASK-2.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Owner agreed Q1-Q7 in chat on 2026-10-24 and asked for model guidance. Each spec carries a model: label (opus for the algorithmic core and triage, sonnet for well-specified areas, haiku for mechanical and gate work). TASK-4 tells the /implement-spec orchestrator to pass the label as the subagent model, because the skill has no model logic of its own. Specs were created by a script; spec N is TASK-4.N. Dependencies were verified in the TASK-4.14 graph.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
TASK-4 now holds 44 specs across milestones m-0 to m-7, with wired dependencies, the shared acceptance criteria and DoD, Owns/Registry/Tests/Seams/Budgets sections, and model: and hitl labels. Every handoff section-7 item is placed: CI in 7 and 43, suspect inputs in 18 and 19, first live runs in 28, generators in 40 and 41, sim and time budgets in 38, BUD-12 in 42. TASK-2.2's open criterion moved to TASK-4.38, and TASK-2.2 and TASK-2 are Done.
<!-- SECTION:FINAL_SUMMARY:END -->
