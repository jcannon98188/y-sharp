---
id: TASK-3.5
title: M0 spike design
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 16:52'
updated_date: '2026-10-24 17:47'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-3.3
references:
  - docs/adr/0018-memory-layout-requirements.md
  - docs/adr/0009-allocation-and-memory-budgets.md
parent_task_id: TASK-3
ordinal: 48000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

What exactly does the M0 spike build and measure for each of the two storage models (object-per-item and indexed arenas): which operations, where the code lives and whether it is thrown away, how the three workloads and the steady-state check are run, what pass/fail and tie-break rules pick the winner, and what the follow-up storage ADR must record?
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Spike scope, location and disposal are decided
- [x] #2 Measurement procedure and the selection rule between the two models are decided
- [x] #3 The contents of the follow-up storage ADR are listed
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Read storage research sections 4-5 and the suite's memory workloads. 2. Grill scope, measurement, selection and ADR contents. 3. Record in ADR 0018 (spike) and ADR 0009 (unit).
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The suite already defines the steady-state mix (MemoryWorkloads.SteadyStateEdits) and divides by operations, which settles the two gaps the research raised. Owner agreed Q1-Q7 in chat on 2026-10-24.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Both models (object-per-item, chunked arenas) get a local-only spike over a shared content layer. A driver interface runs the suite's three workloads and steady-state mix; the harness lives in tests/YSharp.Tests/Allocation, so M2 and M3 rerun it on the real store. Candidates sit in a throwaway spike/YSharp.StoreSpike project. Measurement follows measure.mjs: Release, one process per workload, median of 3. Selection eliminates any failure, prefers headroom, prefers object-per-item when both have 20% headroom, needs sign-off when the winner is 2x slower, and escalates if both fail. The storage ADR contents are listed. ADR 0009 now says bytes per operation and names the steady-state mix. Recorded in ADR 0018 and 0009. Verified by owner review in chat.
<!-- SECTION:FINAL_SUMMARY:END -->
