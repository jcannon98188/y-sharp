---
id: TASK-13
title: 'Fold a stored log into the relay with one merge, not one fold per record'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-26 23:57'
updated_date: '2026-10-27 00:47'
labels:
  - server
  - performance
dependencies: []
ordinal: 69000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found in the performance audit after PR #2: UpdateLog.FoldAsync (src/YSharp.Server/Storage/UpdateLog.cs) folds records one by one with YRelay.TryApplyUpdate, and each fold merges with the whole held state (TASK-9), so loading or compacting a document of N records is O(N x state). Loading happens on every first connect after idle eviction (SRV-20), and on every compaction. Yjs storage backends (y-leveldb, y-redis) merge a stored log with one mergeUpdates over all records. Decode every record, validate each against StoredLimits as now, then merge them in one N-way UpdateMerge.Merge and check the pending bound once. Before relying on it, confirm against the Yjs oracle that one N-way merge gives the same bytes as folding the records one at a time. If it does not, keep the per-record fold and let TASK-9 make each fold cheap.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Loading a log of N records costs O(total size x log) rather than O(N x state)
- [x] #2 The relay's merged state after loading is byte-identical to the per-record fold, checked against the Yjs oracle (mergeUpdates over the same records) on the existing simulation and recovery tests
- [x] #3 A record that does not decode still fails the load with InvalidDataException naming its sequence
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Approach: UpdateLog.FoldAsync (and DocumentMailbox.LoadAsync, the SRV-19 rehydration, which folded the same way) reads every record, merges them with one YUpdate.TryMerge under StoredLimits and folds the merged update into the relay (and the hot Doc) in one step. If the merge or the merged fold is refused (a bad record, or a StoredLimits cap the merged update breaks that no record did) it falls back to the per-record fold, which names the failing record; a refused fold leaves the relay unchanged.
UpdateMerge's N-way merge re-sorted every reader per output struct, as Yjs does, which is O(n) per struct for n inputs. It now keeps the readers in a priority queue ordered by struct then by a stamp: the input index at first, and a value lower than any so far for each reader put back. That reproduces the stable sort exactly (only the front reader moves between passes, and the sort puts it in front of every reader it ties with), at O(log n) per struct.
Equivalence: a scratch probe over 400 RelayWorkload seeds (in-order logs, and shuffled arrivals with duplicates, V1 and V2) found the N-way merge byte-identical to the per-record fold before and after the queue change; RelayDifferentialTests now also runs Yjs mergeUpdates over every arrival and requires the relay's last fold (Smoke: passed).
Cost: a 2400-record log stored newest first allocated 212682 bytes per record with the per-record fold (28438 at 300 records: quadratic); with one merge the per-record figure stays flat (ALoad_CostsTheLogsSize_NotItsRecordsTimesTheState fails on the old code).
IUpdateStore's ReadSinceAsync contract now says a record's bytes stay valid after the read moves past it (UpdateStoreConformance already relied on it), so the load holds the records without copying.
Validation: full Debug suite with YSHARP_RELEASE_GATE=1 (4709, 0 failed), Release allocation namespace and RelayTests, Smoke interop.
Suite changes:
- tests/YSharp.Server.Tests/Storage/UpdateLogTests.cs: ALoad_GivesTheBytesOfFoldingTheRecordsOneAtATime (V1, V2), ALoad_CostsTheLogsSize_NotItsRecordsTimesTheState
- tests/YSharp.Interop.Tests/Tests/RelayDifferentialTests.cs: a mergeAll step (Yjs mergeUpdates over every arrival) checked against the last fold, and YUpdate.TryMerge over the same checked in-process
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
A stored log now loads with one N-way merge and one fold (UpdateLog.FoldAsync and the mailbox's rehydration), with the old per-record fold kept as the fallback that names a bad record. UpdateMerge's N-way loop uses a priority queue that reproduces Yjs's stable-sort order, so a load costs the log's size times log n instead of n times the state. Verified byte-identical to the per-record fold against the Yjs oracle (RelayDifferentialTests) and in UpdateLogTests; full suite green.
<!-- SECTION:FINAL_SUMMARY:END -->
