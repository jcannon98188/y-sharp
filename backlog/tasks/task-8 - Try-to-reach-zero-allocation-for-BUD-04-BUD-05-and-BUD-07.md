---
id: TASK-8
title: 'Try to reach zero allocation for BUD-04, BUD-05 and BUD-07'
status: To Do
assignee: []
created_date: '2026-10-26 20:30'
labels:
  - perf
  - allocation
dependencies:
  - TASK-5.1
  - TASK-5.2
  - TASK-5.3
  - TASK-5.4
  - TASK-5.5
  - TASK-5.6
  - TASK-5.7
ordinal: 63000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
ADR 0009 aimed for zero bytes on three hot paths: event dispatch with no handlers (BUD-04), a commit with no Update subscriber (BUD-05) and a one-character YText.Insert at the end (BUD-07). TASK-4.27 found zero out of reach with the chosen storage (BUD-01 option A): every insert allocates an Item and every transaction its Transaction. It made the three budgets bounded at the Release measurement plus 25% (215, 974 and 606 bytes per operation). On 2026-10-26 the owner approved the bounded limits for now and asked for one more attempt at zero once the server tier (TASK-5 and all its subtasks) is done. If zero stays out of reach, the bounded limits stand; the work is then to tighten the limits to what was reached and record why zero is not possible.
References: docs/adr/0009-allocation-and-memory-budgets.md, docs/inventory/budgets.yaml (BUD-01, BUD-04, BUD-05, BUD-07), tests/YSharp.Tests/Allocation/AllocationBudgets.cs, tests/YSharp.Tests/Allocation/BudgetTableTests.cs.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Each of BUD-04, BUD-05 and BUD-07 is measured again in Release, and the figures are recorded in the task notes
- [ ] #2 Every path where zero is reached has its budget set to zero bytes, with the allocation test asserting zero
- [ ] #3 Every path that stays above zero has its limit tightened to the new measurement plus 25%, and its budgets.yaml note says what still allocates and why it cannot be removed
- [ ] #4 ADR 0009 and the budgets.yaml notes no longer say pending owner sign-off for these three budgets
<!-- AC:END -->
