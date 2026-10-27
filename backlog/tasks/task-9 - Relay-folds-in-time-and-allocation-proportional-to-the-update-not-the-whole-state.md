---
id: TASK-9
title: >-
  Relay folds in time and allocation proportional to the update, not the whole
  state
status: Done
assignee:
  - '@claude'
created_date: '2026-10-26 22:01'
updated_date: '2026-10-27 00:25'
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
- [x] #1 A fold of an update of N bytes into a relay allocates O(N), independent of the held state, outside of a rare compaction
- [x] #2 Relay output stays byte-identical to Yjs mergeUpdates on the existing RelayTests and differential runs
- [x] #3 RelayHostile.BytesPerStateByte is removed or reduced to the compaction case
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Audit after PR #2: once the held state is per client, UpdateMerge.Diff (answering a peer's Step1) can binary-search each client for the first struct past the target's clock instead of walking every struct, so a reconnecting peer that is nearly up to date costs O(clients x log) rather than O(state). The output must stay byte-identical to Yjs diffUpdate.

Per-client form: YRelay holds each client's merged structs (ChunkedList, ADR 0018) with cumulative V1 offsets, Skip indices, state and pending bytes. A canonical update (clients descending, one contiguous block each, as every Yjs writer produces) over regular held runs folds per client: Yjs mergeUpdates writes a regular held run back unchanged, so the merge resumes (UpdateMerge.Merge range+write overload) at the end of the held run the update's first clock falls in. Anything else folds in the old flat form; the next prepare converts back. Diff binary-searches each client (the audit note). PendingBytes is kept per client (struct blocks and clipped delete ranges) and checked against the from-scratch measure on shuffled streams.

API behaviour: PreparedUpdate.StateVector is now built on first read and throws InvalidOperationException if read after the relay committed another update (it was only read right after prepare/commit).

Suite changes (69d2183): RelayTests +3 (pending size per client equals the whole-state measure; in-order folds keep the per-client form and merge no held struct; fold allocation at 1700 updates under 2x that at 300). RelayHostile: BytesPerStateByte (128 per state byte) replaced with BytesPerMergedStruct (768 per held struct the fold merged or copied again, YRelay.MergedHeldStructs): 0 for appends, a client's tail from the gap it fills, or the whole state on the flat path and its conversion back.

Verified: full Debug suite (YSHARP_RELEASE_GATE=1) 0 failed; interop Smoke tier incl. RelayDifferentialTests 118/118.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Relay folds are now proportional to the update: the state is held per client and a canonical update's merge resumes at the end of each touched client's held run, with pending size and state vector kept per client and a binary-search diff. Output stays byte-identical to Yjs mergeUpdates/diffUpdate (RelayTests, differential). Non-canonical input falls back to the flat merge. The fuzz guard's per-state-byte allowance is replaced with a per-re-merged-struct allowance, 0 for appends.
<!-- SECTION:FINAL_SUMMARY:END -->
