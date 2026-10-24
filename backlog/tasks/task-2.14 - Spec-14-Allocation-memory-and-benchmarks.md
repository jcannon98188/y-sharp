---
id: TASK-2.14
title: 'Spec 14: Allocation, memory and benchmarks'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 22:01'
updated_date: '2026-10-24 10:44'
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
- [x] #1 AllocationBudgets table with stable BUD-* IDs and the 11 hot-path tests run in a Release serial collection
- [x] #2 Slow memory tier runs the three workloads and the gen2 and LOH steady-state check, with counts reported
- [x] #3 BenchmarkDotNet project and nightly comparison report regressions without failing the build
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. AllocationBudgets table (BUD-01..14) + hot-path tests in serial Allocation collection. 2. Hostile corpus test (BUD-12). 3. Slow memory tier + Yjs baseline script. 4. BenchmarkDotNet benchmarks + comparer + nightly workflow. 5. Registry notes, matrix, both configs green.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Release and Debug green after merging integration tip (8067 tests, 0 failed; Allocation namespace 661 cases: 22 pass, 639 skip on the API stub). Pending mark stays registry 16 / ledger 0; BUD-01..14 stay pending per ADR 0018/0019. Yjs baseline measured with node 24 / yjs 13.6.33 in bench/yjs-memory.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added AllocationBudgets (BUD-01..14), 11 hot-path tests (16 cases) in the serial Allocation collection, hostile-corpus test (BUD-12), Nightly slow memory tier (3 bytes-per-item workloads vs recorded Yjs figures + gen2/LOH steady-state, reports under TestResults/memory), BenchmarkDotNet project with compare/table commands (never fails), nightly workflow wiring and Spec 14 matrix. Verified: dotnet test Debug and Release both 0 failed; comparer unit tests pass; behavioural tests skip visibly against the API stub.
<!-- SECTION:FINAL_SUMMARY:END -->
