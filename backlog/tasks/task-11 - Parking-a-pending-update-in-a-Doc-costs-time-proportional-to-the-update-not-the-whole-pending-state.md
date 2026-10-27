---
id: TASK-11
title: >-
  Parking a pending update in a Doc costs time proportional to the update, not
  the whole pending state
status: Done
assignee:
  - '@claude'
created_date: '2026-10-26 23:41'
updated_date: '2026-10-27 00:13'
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
- [x] #1 Parking an update of N bytes costs O(N log clients) in time and allocation, independent of the pending state already held
- [x] #2 The predicted pending size stays exact (the Debug.Assert in TryIntegrateRemote still holds) on the existing pending and fuzz tests
- [x] #3 The pending loop in ThousandsOfTinyPendingUpdates scales linearly (doubling the count about doubles the time)
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. PendingStructs keeps each client's V1 size (struct count incl. implied Skips, and the body: client, first clock, structs, Skips) and a running total, updated in Set and MergeFrom (ToUpdate hands the lists over and the state is dropped, so it needs no update). A merge whose structs all start at or after the client's last waiting struct appends in place (the same result Normalise gives); anything else is normalised into a new list as before. A Clone (only a dry retry makes one) copies a shared list before appending.
2. The dry run no longer clones: DryReadUpdate takes the real pending state read-only and returns the predicted struct bytes as base total plus, per client of the update's rest, the merged size minus the held size (an append is measured from the rest alone). Only the retry branch materialises a merged copy, since the retry re-reads the whole pending state anyway (Yjs applyUpdateV2(pendingStructs.update)).
3. The retry check (some missing clock now below its client's state) stops scanning every Missing entry: the store flags when a struct it adds passes an awaited clock of the installed pending state, the Doc checks newly merged Missing entries against the state, and the dry run checks only the clients it placed. Debug builds assert the flag equals the full scan. Integration order is unchanged.
4. The pending delete set is still re-applied per update as Yjs does; its V1 size is only encoded when deletes are pending.
5. Measure the AmplificationTests pending loop at 1500/3000/6000 in Release; run the full suite.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implemented in f33993a (src) and ad7fcbd (suite). Branch rebased by fast-forward onto origin/main e8ecae1 (PR #2 merged) before committing; the src files were identical there.

Timing (Release, Windows, scratch console app replaying the AmplificationTests.ThousandsOfTinyPendingUpdates shape: N one-char updates from distinct clients, each waiting on (7000000, 0); park loop only, 5 runs):
- before (origin/main e8ecae1): 1500 = 0.25-0.73 s, 3000 = 0.77-1.06 s, 6000 = 3.13-3.36 s (quadratic)
- after: 1500 = 0.014-0.026 s, 3000 = 0.033-0.050 s, 6000 = 0.066-0.083 s, 12000 = 0.042-0.072 s, 24000 = 0.083-0.095 s, 48000 = 0.137-0.177 s (linear; doubling about doubles). The retry when the dependency arrives stays linear (0.2 s total at 48000).

Decisions:
- The retry check used to scan every Missing entry per update (O(pending) when many distinct clients are missing). Now StructStore.AddStruct sets AwaitedPassed when a struct ends past an awaited clock of the installed pending state (StructStore.Awaited = _pending.Missing); merged-in missing clocks are checked on arrival; the dry run checks Store.AwaitedPassed plus only the clients it placed. States only grow and missing clocks only fall, so the flag equals the scan; Debug.Assert checks this in ReadUpdate and DryReadUpdate.
- In-place append on MergeFrom when the incoming structs all start at or after the client's last waiting struct (the same result Normalise gives). Clone (used only by a dry retry) marks its lists as shared, so it copies before appending.
- Doc.V1Bytes made internal for the suite test; it still backs the Debug.Assert in TryIntegrateRemote.

Caveats (not changed, same as Yjs): the pending delete set is still re-applied (and, in the dry run, re-measured) on every update, so updates cost O(pending delete ranges) while deletes are pending. A struct that lands before a client's last waiting struct still re-normalises that client's list (O(that client's pending)).

Suite changes (ADR 0022): ad7fcbd adds IntegrationEngineTests.ThePredictedPendingSize_IsExactlyWhatTheUpdateLeaves_ForRandomWaitingShapes (300 seeds x 30 steps; reject at expected-1, accept at expected). A mutation in the Skip accounting made it fail at seed 0 step 6.

Validation: dotnet build YSharp.slnx -c Debug (0 warnings); YSHARP_RELEASE_GATE=1 dotnet test --solution YSharp.slnx -c Debug --no-build: 4687 total, 0 failed, 778 skipped (tiers); YSharp.Fuzz.Tests in -c Release with YSHARP_RELEASE_GATE=1: 188 total, 0 failed, 10 skipped. Note: this worktree needed git submodule init (yjs, y-protocols, lib0) from the main checkout before the oracle-backed tests could run.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Parking a pending update no longer clones and re-encodes the whole pending state. PendingStructs keeps per-client V1 sizes and a running total; the MaxPendingBytes dry run measures the merge against the held state without copying it (only a retry copies); and a store flag replaces the per-update scan of every missing clock. The prediction stays exact (Debug.Assert, plus a new 300-seed differential suite test that runs in Release), and integration order and emitted bytes are unchanged. The ThousandsOfTinyPendingUpdates park loop went from quadratic (0.25/0.8-1.06/3.1-3.4 s at 1500/3000/6000) to linear (0.015/0.035/0.075 s; 0.16 s at 48000). Full Debug suite and Release Fuzz pass. Pending delete ranges are still re-applied per update, as in Yjs.
<!-- SECTION:FINAL_SUMMARY:END -->
