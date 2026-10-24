---
id: TASK-3.7
title: Rules for changing the suite during implementation
status: To Do
assignee: []
created_date: '2026-10-24 16:52'
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
- [ ] #1 Who may edit tests, registry entries, fixtures and budgets, and how each change is recorded, is decided
- [ ] #2 The rules are phrased so they can go into every plan spec's definition of done
<!-- AC:END -->
