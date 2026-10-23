---
id: TASK-1.15
title: Allocation budgets and named hot paths
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 19:55'
updated_date: '2026-10-23 20:57'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.14
parent_task_id: TASK-1
ordinal: 16000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

Decide which hot paths get allocation assertions (decode, apply, encode, text insert...), the budgets (zero/bounded), how they are measured reliably in xUnit, and what goes only in BenchmarkDotNet.
<!-- SECTION:DESCRIPTION:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Adopted (user: implementation must pay close attention to GC pressure and memory): add a bytes-per-item memory budget for large docs, a lock-overhead budget for small transactions, and BenchmarkDotNet comparisons against Yjs on shared golden workloads.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided the allocation/memory budget policy: 11 named hot paths (7 zero, 4 bounded), plain xUnit helper over GC.GetAllocatedBytesForCurrentThread in Release with TieredCompilation off and an EventListener diagnostic, no wall-clock gates, ratcheted bounded budgets in a central ID table, hostile-input allocation bound, slow-tier heap-per-item and gen2/LOH checks capped by Yjs's figures, report-only BenchmarkDotNet. Recorded in docs/adr/0009-allocation-and-memory-budgets.md. Decision only; nothing to execute.
<!-- SECTION:FINAL_SUMMARY:END -->
