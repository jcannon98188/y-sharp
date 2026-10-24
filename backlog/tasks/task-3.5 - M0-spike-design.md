---
id: TASK-3.5
title: M0 spike design
status: To Do
assignee: []
created_date: '2026-10-24 16:52'
updated_date: '2026-10-24 17:00'
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
- [ ] #1 Spike scope, location and disposal are decided
- [ ] #2 Measurement procedure and the selection rule between the two models are decided
- [ ] #3 The contents of the follow-up storage ADR are listed
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
From the research: the spike must name the unit of "bytes per item" (per op vs per struct; they diverge 1M:1 for the array workload), define the 100k steady-state edit mix ADR 0009 leaves open, and meet 8.31 B/op for array pushes (needs a homogeneous 8-byte chunked column). The spike needs a private harness; the public API cannot drive the workloads before types and transactions exist. See docs/research/storage-layout.md sections 4-5.
<!-- SECTION:NOTES:END -->
