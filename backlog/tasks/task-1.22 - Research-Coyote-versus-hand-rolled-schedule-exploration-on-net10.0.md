---
id: TASK-1.22
title: 'Research: Coyote versus hand-rolled schedule exploration on net10.0'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 21:31'
updated_date: '2026-10-23 21:35'
labels:
  - 'wayfinder:research'
dependencies: []
references:
  - docs/adr/0006-concurrency-contract.md
parent_task_id: TASK-1
ordinal: 23000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
ADR 0006 needs stress and race tests; the choice of schedule-exploration tool is open and must work on net10.0 and xUnit v3.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Findings in docs/research/schedule-exploration.md: Coyote compatibility with net10.0 and xUnit v3, cost of a hand-rolled scheduler, recommendation
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Researched Coyote against a hand-rolled scheduler for ADR 0006 stress tests. Recommendation: hand-rolled seeded cooperative scheduler with yield/block seams; Coyote 1.7.11 targets net8.0, net10 support is an unmerged PR, RWLS is uncontrolled, xUnit v3 compatibility unverified. Findings in docs/research/schedule-exploration.md.
<!-- SECTION:FINAL_SUMMARY:END -->
