---
id: TASK-9
title: >-
  Relay folds in time and allocation proportional to the update, not the whole
  state
status: To Do
assignee: []
created_date: '2026-10-26 22:01'
updated_date: '2026-10-26 23:57'
labels:
  - server
  - performance
dependencies: []
ordinal: 65000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found in TASK-5.3: YRelay.TryApplyUpdate merges the incoming structs with the whole held list (UpdateMerge.Merge) and re-measures the pending bound on every fold, so each fold is O(state) in time and allocation and a long stream of small updates is quadratic. The fuzz guard allows 128 bytes per state byte for this (tests/YSharp.Fuzz.Tests/Guards/RelayHostile.cs). Fold in place (append past the client's clock, merge only overlapping clients) and keep the pending size incrementally.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A fold of an update of N bytes into a relay allocates O(N), independent of the held state, outside of a rare compaction
- [ ] #2 Relay output stays byte-identical to Yjs mergeUpdates on the existing RelayTests and differential runs
- [ ] #3 RelayHostile.BytesPerStateByte is removed or reduced to the compaction case
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Audit after PR #2: once the held state is per client, UpdateMerge.Diff (answering a peer's Step1) can binary-search each client for the first struct past the target's clock instead of walking every struct, so a reconnecting peer that is nearly up to date costs O(clients x log) rather than O(state). The output must stay byte-identical to Yjs diffUpdate.
<!-- SECTION:NOTES:END -->
