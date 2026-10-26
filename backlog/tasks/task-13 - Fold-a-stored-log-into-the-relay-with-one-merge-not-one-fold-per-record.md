---
id: TASK-13
title: 'Fold a stored log into the relay with one merge, not one fold per record'
status: To Do
assignee: []
created_date: '2026-10-26 23:57'
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
- [ ] #1 Loading a log of N records costs O(total size x log) rather than O(N x state)
- [ ] #2 The relay's merged state after loading is byte-identical to the per-record fold, checked against the Yjs oracle (mergeUpdates over the same records) on the existing simulation and recovery tests
- [ ] #3 A record that does not decode still fails the load with InvalidDataException naming its sequence
<!-- AC:END -->
