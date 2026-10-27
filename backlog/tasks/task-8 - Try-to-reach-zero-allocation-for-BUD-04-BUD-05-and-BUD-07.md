---
id: TASK-8
title: 'Try to reach zero allocation for BUD-04, BUD-05 and BUD-07'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-26 20:30'
updated_date: '2026-10-27 00:38'
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
- [x] #1 Each of BUD-04, BUD-05 and BUD-07 is measured again in Release, and the figures are recorded in the task notes
- [x] #2 Every path where zero is reached has its budget set to zero bytes, with the allocation test asserting zero
- [x] #3 Every path that stays above zero has its limit tightened to the new measurement plus 25%, and its budgets.yaml note says what still allocates and why it cannot be removed
- [x] #4 ADR 0009 and the budgets.yaml notes no longer say pending owner sign-off for these three budgets
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Release measurements (HotPathBudgetTests window: fresh populated doc, 50 warm-up, 200 measured): BUD-04 151 bytes/op (was 215), BUD-05 118 (was 974), BUD-07 118 (was 606); BUD-09:Set also fell to 136 (was 200), BUD-09:Push unchanged at 128.
Changes: YText.Insert/Delete enter the write directly instead of through a closure (a lambda over parameters allocates its display class on method entry even when unused), and the not-yet-attached path moved to helpers; text edits rent the document's spare TypeText.Position (and its Current map and spare Attributes) and give it back; InsertAttributes, CleanupFormattingGap and DeleteText make their dictionaries only when there are formats; KeyTable caches each key's string so parentSub naming stops allocating.
No path reached zero (AC #2 holds vacuously): each insert's Item and the 24-byte Transaction (BUD-01 option A) remain, plus amortized growth of the store's buffers (a 200-op window can catch a doubling, which is why a short-window probe read 294 for BUD-04 while a 20000-op window reads 132).
Validation: Debug full suite with YSHARP_RELEASE_GATE=1 (4706 total, 0 failed), Release YSharp.Tests (4274, 0 failed), Smoke-tier interop (118 passed, 1 skipped).
Suite changes:
- tests/YSharp.Tests/Allocation/AllocationBudgets.cs: BUD-04 WithHeadroom(151), BUD-05 WithHeadroom(118), BUD-07 WithHeadroom(118), BUD-09:Set WithHeadroom(136), comments updated.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Cut BUD-05 from 974 to 118 bytes/op, BUD-07 from 606 to 118, BUD-04 from 215 to 151 (and BUD-09:Set from 200 to 136) by removing closures from YText.Insert/Delete, reusing the text position and attributes, making format dictionaries lazily and caching key strings. Zero is not reachable (Item plus Transaction, option A), so the limits were tightened to the new figures plus 25% and ADR 0009/budgets.yaml say why. Verified by the Release HotPathBudgetTests, the full Debug suite and Smoke interop.
<!-- SECTION:FINAL_SUMMARY:END -->
