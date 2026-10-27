---
id: TASK-11
title: >-
  Parking a pending update in a Doc costs time proportional to the update, not
  the whole pending state
status: In Progress
assignee:
  - '@claude'
created_date: '2026-10-26 23:41'
updated_date: '2026-10-26 23:57'
labels:
  - core
  - performance
dependencies: []
ordinal: 67000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found fixing CI on PR #2 (macOS Release): Doc.TryIntegrateRemote predicts the pending size for MaxPendingBytes by cloning the whole pending state (PendingStructs.Clone, O(clients)) and re-encoding all of it to V1 (Doc.V1Bytes) on every update that can wait. So parking N updates is near-quadratic: locally 0.66 s, 1.93 s and 6.69 s for 1500, 3000 and 6000 tiny updates from distinct clients (AmplificationTests.ThousandsOfTinyPendingUpdates). With the default 64 MB MaxPendingBytes, a peer can drive this on a server document with a validation hook. Yjs is O(pending) per apply too (mergeUpdatesV2 of the pending update), so this is not an interop gap. Keep the pending V1 size incrementally (per-client sizes plus the header) and make the dry run copy-on-write.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Parking an update of N bytes costs O(N log clients) in time and allocation, independent of the pending state already held
- [ ] #2 The predicted pending size stays exact (the Debug.Assert in TryIntegrateRemote still holds) on the existing pending and fuzz tests
- [ ] #3 The pending loop in ThousandsOfTinyPendingUpdates scales linearly (doubling the count about doubles the time)
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. PendingStructs keeps each client's V1 size (struct count incl. implied Skips, and the body: client, first clock, structs, Skips) and a running total, updated in Set, MergeFrom and ToUpdate. A merge whose structs all follow the client's last pending struct appends in place (the lists are only shared with a dry run while it runs, and the dry run never changes them); anything else is normalised into a new list as now.
2. The dry run no longer clones: DryReadUpdate takes the real pending state read-only and returns the predicted struct bytes as base total plus, per client of the update's rest, the merged size minus the held size (an append is measured from the rest alone). Only the retry branch materialises a merged copy, since the retry re-reads the whole pending state anyway (Yjs applyUpdateV2(pendingStructs.update)).
3. The retry check (some missing clock now below its client's state) stops scanning every Missing entry: the store flags when a struct it adds passes an awaited clock of the installed pending state, the Doc checks newly merged Missing entries against the state, and the dry run checks only the clients it placed. Debug builds assert the flag equals the full scan. Integration order is unchanged.
4. The pending delete set is still re-applied per update as Yjs does; its V1 size is only encoded when deletes are pending.
5. Measure AmplificationTests pending loop at 1500/3000/6000 in Release; run the full suite.
<!-- SECTION:PLAN:END -->
