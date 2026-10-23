---
id: TASK-1.23
title: Port plan for the randomized JS tests
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 21:31'
updated_date: '2026-10-23 21:46'
labels:
  - 'wayfinder:grilling'
dependencies: []
references:
  - docs/research/js-tests.md
parent_task_id: TASK-1
ordinal: 24000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
About 100 upstream tests use the multi-client simulation and several are repetition-count tests. The PRNG is ported bit-exact (ADR 0015); repetition counts, seeds and which tests also get recorded op-log fixtures are open.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Repetition counts and seeds per randomized test are decided for gating and nightly tiers
- [x] #2 Which ported tests also have golden op-log fixtures is decided
- [x] #3 Recorded as an ADR or an addendum to ADR 0010
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Decided with the user in four grilling questions; recorded as docs/adr/0017-randomized-test-port-plan.md (gating seed lists by size class, nightly time-boxed random seeds, 12 golden op logs, SIM-01..05 defect fixes). Verified: ADR file exists and each criterion maps to a bullet (reps/seeds; fixtures; ADR).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Port plan for the randomized JS tests recorded in ADR 0017: fixed seed lists by size class for gating, time-boxed random seeds nightly, 12 golden op-log fixtures (4 generators x 3 sizes) with a draw-parity test, and simulation defects fixed as SIM-01..05.
<!-- SECTION:FINAL_SUMMARY:END -->
