---
id: TASK-2.14
title: 'Spec 14: Allocation, memory and benchmarks'
status: To Do
assignee: []
created_date: '2026-10-23 22:01'
labels: []
dependencies:
  - TASK-2.2
  - TASK-2.5
  - TASK-2.7
references:
  - docs/adr/0009-allocation-and-memory-budgets.md
  - docs/adr/0018-memory-layout-requirements.md
parent_task_id: TASK-2
ordinal: 41000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 3. Budgets stay pending until the implementation exists (ADR 0018).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 AllocationBudgets table with stable BUD-* IDs and the 11 hot-path tests run in a Release serial collection
- [ ] #2 Slow memory tier runs the three workloads and the gen2 and LOH steady-state check, with counts reported
- [ ] #3 BenchmarkDotNet project and nightly comparison report regressions without failing the build
<!-- AC:END -->
