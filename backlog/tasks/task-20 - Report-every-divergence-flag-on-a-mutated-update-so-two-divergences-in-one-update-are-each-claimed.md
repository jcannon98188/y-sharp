---
id: TASK-20
title: >-
  Report every divergence flag on a mutated update, so two divergences in one
  update are each claimed
status: Done
assignee:
  - '@claude'
created_date: '2026-10-27 18:46'
updated_date: '2026-10-27 19:42'
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
- [x] #3 The Nightly tier passes on main
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Set each oracle flag independently in steps-foreign.mjs. 2. Pin both flags and the normaliser claim on the nightly's hex. 3. Gate, PR, nightly.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
steps-foreign.mjs now sets each flag independently. Each normaliser already checks y-sharp's side for itself (accepted, unchanged), so a case carrying several flags is claimed by the one whose conditions hold; Claim keeps registry order. ApplyForeign_AnUpdateCarryingTwoDivergences_IsFlaggedForBoth fails against the old step (only unsortedPendingDeleteSet) and passes now. Fixture check unchanged.

Nightly run 40564569431 on main 32ac03f passed (all tiers, HOS-MUT included). Fixture check: 143 files up to date.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
The oracle's named applyForeign result now carries every divergence flag that holds, so an update carrying two recorded divergences (STR-71 and ENC-86) is claimed by the right normaliser (PR #11). Harness-only. Verified by a regression test that fails against the old step, the release gate, Smoke, and the passing nightly run 40564569431.
<!-- SECTION:FINAL_SUMMARY:END -->
