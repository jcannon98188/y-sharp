---
id: TASK-12
title: Retry pending deletes only for clients whose state advanced
status: Done
assignee:
  - '@claude'
created_date: '2026-10-26 23:57'
updated_date: '2026-10-27 02:09'
labels:
  - core
  - performance
dependencies:
  - TASK-11
ordinal: 68000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found in the performance audit after PR #2: Doc.ReadUpdate (src/YSharp/Integration/Doc.Integration.cs) re-applies the whole pending delete set (ApplyDeleteSet on InWriteOrder(_pendingDeletes)) on every update, so each update costs O(pending deletes) even when nothing it brings can resolve them. A pending range always starts at or past its client's state, so if that client's state has not advanced in this update, re-applying leaves the range exactly as it was. Re-apply only the ranges of clients whose state advanced, and keep the rest as they are. Yjs re-reads all of store.pendingDs each time; the resulting document, pending state and encodings are the same either way, so this is not visible to peers. The MaxPendingBytes dry run (Doc.PendingLimit.cs, Unapplied) mirrors this loop and must change in step. After TASK-11, which reworks the pending accounting in the same files.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 An update costs O(its own size) in pending-delete work when no pending range's client advanced
- [x] #2 The pending delete set, transaction delete sets and every encoding stay identical to before on the existing pending, differential and fuzz tests
- [x] #3 The MaxPendingBytes prediction stays exact
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implementation (daafa0b): a pending range starts at or past its client's state at the end of ReadUpdate, so only a client whose state can advance can resolve one. The candidates are the update's struct clients (collected before IntegrateStructs, which consumes them), ClientId, and client ids the Doc gave up at transaction cleanup while deletes were pending (Doc.RetireClientId; cleared after each ReadUpdate). SettlePendingDeletes (Doc.PendingDeletes.cs) walks those clients and the dsRest clients descending, re-applies the prefix of each client's ranges below its state (ApplyDeleteSet, same DeleteDeep order, so transaction delete sets are unchanged), and merges the tails and the update's unapplied ranges in place (DeleteRanges.RemoveRange). The V1 size of the set is tracked incrementally (PendingDeleteSize: client count + body bytes), so PredictPendingBytes reads it instead of re-encoding; the dry run computes the size only, and on a retry changes a Clone. Caveat: the dry run still reads the ranges a reached client swallows or keeps (O(ranges of reached clients)), never the whole set.

Verification: AnUpdate_AllocatesInProportionToItself_NotToThePendingDeletesItDoesNotReach fails on the old loop (3.9 MB allocated per update over a large pending set) and passes now (~140 KB). The model check (300 seeds x 40 steps) compares the set with Yjs re-reading every range and checks the prediction exactly; a temporary shadow check against the old algorithm in ReadUpdate found no mismatch over full suite runs (removed). Debug assert in TryIntegrateRemote: the tracked size equals DeleteSetBytes. Full gated suite 5051/0, Smoke 119/0, Release allocation 710/0, API surface unchanged.

Suite changes (5a0f4f5): IntegrationEngineTests gains the model check, the local-client and retired-client-id reach tests and the allocation bound. ServerSimulationTests: seed 12 (hooked) failed about once in 13 runs under load, unrelated to this task: a peer applying a remote format change deletes the redundant format items (Yjs cleanupYTextAfterTransaction) as its own update, and the test Peer only sent local updates with a later edit, so one made in the final rounds never left the peer. Peer gains HasUnsent/SendUnsentAsync (a provider sends every update it did not receive) and the run settles until none is unsent (15/15 runs under load after). The direct-Doc reference is compared by content (formatted text, array JSON, map entries by key): its own cleanup deletes depend on delivery order, and object members compare in each replica's key order.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
An update now settles only the pending delete ranges of clients it can advance (its struct clients, the local client, retired client ids) and merges the rest in place, with the V1 size tracked incrementally for the MaxPendingBytes prediction. Verified by an allocation test that fails on the old loop (3.9 MB vs ~140 KB per update), a 300-seed model check against re-reading every range with exact prediction, a shadow comparison with the old algorithm, and the full gated suite (5051/0). Also fixed a simulation flake: the test Peer never sent format-cleanup deletes made in the final rounds.
<!-- SECTION:FINAL_SUMMARY:END -->
