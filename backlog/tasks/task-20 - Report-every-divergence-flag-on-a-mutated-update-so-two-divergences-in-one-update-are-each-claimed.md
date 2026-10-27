---
id: TASK-20
title: >-
  Report every divergence flag on a mutated update, so two divergences in one
  update are each claimed
status: In Progress
assignee:
  - '@claude'
created_date: '2026-10-27 18:46'
updated_date: '2026-10-27 18:46'
labels:
  - bug
  - interop
  - harness
dependencies: []
priority: high
ordinal: 80000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Nightly run 40556356416 on 72638b5 failed Differential_MutatedValidUpdates_FallIntoAKnownOutcomeClass (InsertByte at 23, V1, case seed 1032223761). The mutated update holds a struct (101,102) whose parent (101,0) is an Any item, which Yjs integrates as a GC and y-sharp rejects (STR-71), and out-of-order delete ranges for unknown client 109 that Yjs parks (ENC-86). The oracle's applyForeign step reported only the first flag in priority order (unsortedPendingDeleteSet), so the parent-not-a-type normaliser never saw its flag and the case was reported as an unregistered divergence. Not a y-sharp bug: both divergences are recorded.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The oracle's named applyForeign result carries every flag that holds, not only the first
- [x] #2 A regression test pins both flags on the nightly's hex and the parent-not-a-type normaliser claiming it
- [ ] #3 The Nightly tier passes on main
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
steps-foreign.mjs now sets each flag independently. Each normaliser already checks y-sharp's side for itself (accepted, unchanged), so a case carrying several flags is claimed by the one whose conditions hold; Claim keeps registry order. ApplyForeign_AnUpdateCarryingTwoDivergences_IsFlaggedForBoth fails against the old step (only unsortedPendingDeleteSet) and passes now. Fixture check unchanged.
<!-- SECTION:NOTES:END -->
