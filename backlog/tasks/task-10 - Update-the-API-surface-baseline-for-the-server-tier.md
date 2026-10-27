---
id: TASK-10
title: Update the API surface baseline for the server tier
status: Done
assignee:
  - '@claude'
created_date: '2026-10-26 22:02'
updated_date: '2026-10-27 04:34'
labels:
  - api
dependencies: []
ordinal: 66000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
tools/api-surface/check.sh reports the baseline is stale since TASK-5.2: YRelay (now with TryPrepareUpdate, Commit, PreparedUpdate), YUpdate.TryDecode and DecodedUpdate, YSync.TryReadFrame and the span WriteStep1/WriteStep2 overloads are not in it. The baseline changes are an owner review (public API).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The baseline lists the current public API of YSharp and YSharp.Server and check.sh passes
- [x] #2 Each added member is reviewed against docs/api-decisions.md
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Extend tools/api-surface/check.sh to dump YSharp.Server and YSharp.Server.Testing into a second baseline (baseline.server.txt), keeping --update.
2. Diff the YSharp surface against baseline.txt and list every added or changed member.
3. Review each against docs/api-decisions.md and the core's API conventions (Try/throwing pairs, YStatus, IBufferWriter overloads, sealed classes); record the review in a new section of docs/api-decisions.md for the owner.
4. Rewrite both baselines with --update and confirm check.sh passes.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
check.sh now dumps YSharp into baseline.txt and YSharp.Server plus YSharp.Server.Testing into baseline.server.txt (Program.cs takes several assemblies). Review in docs/api-decisions.md section E: E1 TryPrepareUpdate parameter order, E2 no throwing YRelay.ApplyUpdate, E3 YServer delete/compact convenience are open owner decisions; E4 lists TASK-5's additions; E5 records two pre-existing drifts from TASK-4 (StackItem and YSnapshot lost public ctors; YSnapshot gained Equals/GetHashCode). Verified: check.sh --update, then check.sh reports both surfaces unchanged (810 and 193 lines).

Owner sign-off (2026-10-27): E1 reorder, E2 Try-only, E3 add YServer.DeleteAsync, E6 keep, all as recommended. Implemented in 9ede289 (src, docs, baselines) and 4730c42 (suite). E1: TryPrepareUpdate(update, out prepared, format = V1). E2: YRelay class doc says why it is Try-only. E3: DeleteAsync drains the mailbox, closes connections with the new ConnectionCloseReason.DocumentDeleted, deletes from the store; opens of the document wait meanwhile; no CompactAsync. Suite changes (ADR 0022): call sites of TryPrepareUpdate take the new order; GatedStore gains a delete gate; LifecycleTests gains four deletion tests. Validation: full gated suite 5055 total, 0 failed; API check unchanged after --update.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
API baselines now cover the core and the server tier, and check.sh passes. Every member added since the TASK-4 baseline is reviewed in docs/api-decisions.md section E; E1 to E3 are owner decisions (none changes behaviour until decided).
<!-- SECTION:FINAL_SUMMARY:END -->
