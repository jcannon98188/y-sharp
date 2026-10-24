---
id: TASK-3.7
title: Rules for changing the suite during implementation
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 16:52'
updated_date: '2026-10-24 19:36'
labels:
  - 'wayfinder:grilling'
dependencies: []
references:
  - docs/adr/0003-match-js-by-default.md
  - docs/adr/0010-traceability-registry.md
  - docs/adr/0009-allocation-and-memory-budgets.md
parent_task_id: TASK-3
ordinal: 50000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

When an implementing agent finds a test expectation it believes is wrong, an unrecorded divergence from a live Node run, a fixture that needs regenerating, or a budget value to record, what may it change, how is it recorded (registry, ADR 0003 divergence, `AllocationBudgets`, ledger), and who approves? Includes the two known suspect inputs (`Q22_ANegativeClockFromAV2DiffColumn_IsMalformed` bytes, the `LimitsTests` pending-bytes boundary) and the rule that pending never grows.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Who may edit tests, registry entries, fixtures and budgets, and how each change is recorded, is decided
- [x] #2 The rules are phrased so they can go into every plan spec's definition of done
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Read the change rules in ADRs 0003, 0009, 0010, 0011 and 0012. 2. Grill who may change what, and on what evidence. 3. Record as an ADR.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Owner agreed Q1-Q8 in chat on 2026-10-24.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Three levels (free, with evidence, owner). The oracle decides disputed tests; tests of y-sharp's own decisions need the owner. A new divergence needs the owner, except crash or unbounded recursion. Registry additions with tests are free, pending never grows, and retire/exclude/kind changes need the owner. Fixtures change only via the generator. First budget values are recorded with evidence; raising a budget needs the owner. Any weakening needs the owner, except that time budgets and GatingSeeds are calibrated in M7. Owner questions go in a Backlog comment and block Done. Suite changes go in separate suite: commits with a Suite changes list in the notes, as part of every spec's DoD. Recorded in ADR 0022. Verified by owner review in chat.
<!-- SECTION:FINAL_SUMMARY:END -->
