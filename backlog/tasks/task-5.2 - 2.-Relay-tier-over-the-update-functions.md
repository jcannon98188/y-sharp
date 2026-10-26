---
id: TASK-5.2
title: 2. Relay tier over the update functions
status: Done
assignee:
  - '@claude'
created_date: '2026-10-25 17:38'
updated_date: '2026-10-26 21:00'
labels:
  - server
  - 'model:sonnet'
dependencies:
  - TASK-5.1
parent_task_id: TASK-5
ordinal: 55000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Most server work is receive, persist and fan out, and needs no Doc. The core already has YUpdate.TryMerge, TryDiff and TryStateVectorFromUpdate (spec 16) with YLimits checks, but they are stateless functions. This task adds a small stateful relay type that keeps a document's merged update and state vector and answers sync requests without an Item graph. **Suggested model:** sonnet (override freely).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A relay type accepts update bytes, validates them against YLimits, folds them into its merged update and state vector, and returns a diff for a client state vector, without constructing a Doc
- [x] #2 Its merged output and diffs are byte-identical to the Doc-based path (EncodeStateAsUpdate, ApplyUpdate) on the existing fixtures and under differential fuzzing, for V1 and V2
- [x] #3 Invalid or over-limit updates return a YStatus error and leave the relay state unchanged
- [x] #4 Tests cover empty relay, out-of-order and duplicate updates, gaps that become Skips, and V1 and V2 inputs
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Add YRelay in src/YSharp/Codecs/UpdateFunctions (ADR 0024 SRV-1): hold the merged struct list, delete set and state vector; fold each validated update with UpdateMerge.Merge; answer diffs with UpdateMerge.Diff; write V1 or V2.
2. Record its guarantees as RLY-1..6 in ADR 0024 and docs/inventory/server.yaml.
3. Unit tests (empty relay, every fold = mergeUpdates, Doc parity, diffs, gaps to Skips, out-of-order and duplicates, V1/V2, invalid and over-limit, malformed state vector, concurrency) plus a relay pass over the oracle merge/diff fixtures.
4. Smoke differential: every fold and diff against Yjs mergeUpdates/diffUpdate on seeded workloads.
5. Full suite with the release gate.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
AC #2 reading: the relay's raw merged bytes differ from a Doc's EncodeStateAsUpdate because the relay keeps deleted content that a Doc garbage-collects (Yjs mergeUpdates behaves the same). Parity is therefore checked two ways: each fold and diff is byte-identical to Yjs mergeUpdates([state, update]) / diffUpdate (unit, fixtures and differential), and a Doc built from the relay's state has the same EncodeStateAsUpdate bytes (V1 and V2) and state vector as a Doc built from every input.
RLY-* inventory entries use owner 5 (the inventory owner field takes a spec number 1..14; 5 is the update-functions spec the relay extends) and kind y-sharp-own.
Lock: a plain object monitor, since ADR 0006 CC-5 forbids System.Threading.Lock fields (LockModelTests caught it).
Core changes: YUpdate.StateVectorEntries extracted; InWriteOrder and Write made internal for the relay.
Suite changes (ADR 0022): new tests/YSharp.Tests/Codecs/RelayTests.cs, tests/YSharp.Interop.Tests/Tests/RelayDifferentialTests.cs, tests/YSharp.TestKit/Relay/RelayWorkload.cs, docs/inventory/server.yaml, RLY-1..6 appended to docs/inventory/id-lock.txt. No existing test changed.
Validation: full suite with YSHARP_RELEASE_GATE=1 (Debug) 4505 total, 0 failed, 776 skipped (Node tiers); RelayTests 56/56; RelayDifferentialTests Smoke with YSHARP_REQUIRE_NODE=1 passed (20 seeded cases).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added YRelay, a Doc-less relay that folds V1/V2 updates into a merged update and state vector and answers diffs for client state vectors (ADR 0024 RLY-1..6). Verified byte for byte against Yjs mergeUpdates/diffUpdate on the oracle fixtures and in a Smoke differential, with Doc parity in V1 and V2, rejection without state change, and thread safety; full suite green with the release gate.
<!-- SECTION:FINAL_SUMMARY:END -->
