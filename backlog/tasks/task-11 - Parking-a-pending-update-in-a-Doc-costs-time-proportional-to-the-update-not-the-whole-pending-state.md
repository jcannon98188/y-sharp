---
id: TASK-11
title: >-
  Parking a pending update in a Doc costs time proportional to the update, not
  the whole pending state
status: To Do
assignee: []
created_date: '2026-10-26 23:41'
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
