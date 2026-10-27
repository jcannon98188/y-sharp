---
id: TASK-18
title: >-
  Reload a stored log record by record, so a hostile update reusing an ID cannot
  change the document after a restart (0.1.0-preview.3)
status: In Progress
assignee:
  - '@claude'
created_date: '2026-10-27 14:49'
updated_date: '2026-10-27 16:48'
labels:
  - bug
  - server
  - persistence
dependencies: []
priority: high
ordinal: 78000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The nightly run on main (run 40501680613, 2026-10-27) fails two HOS-MUT cases in ServerTierBoundsTests, "the store and the relay disagree after a restart". Live, the relay folds each update in arrival order and skips a struct whose ID it already holds, so the first content at an ID wins; the store keeps the peer bytes verbatim. On reload, UpdateLog.FoldAsync merges every record with YUpdate.TryMerge (Yjs mergeUpdates, TASK-13) and folds the result once, assuming the merge equals folding the records one at a time. That holds only when no two records hold different content at one ID, which honest Yjs never sends; a hostile or broken peer can, and the merge can let the later struct win (for example a GC over a real item). After a restart, peers that join get a different document from those still connected, and CompactAsync makes the wrong winner permanent. preview.1 and preview.2 both have it. The owner chose folding record by record over storing only the integrated part of each update (store-side trimming risks silent data loss on pending structs and delete sets and does not repair logs already written); trimming may come later as an optimisation. Repro inputs (V1, through the mailbox): 02010200c40000000001000101000001000000000000 (Duplicate of structs/pending-two-missing-clients.json:e) and 030103008400000100010200840000010001010000010000000000 (Overwrite of structs/pending-chain-permutations.json:d).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A reloaded document (LoadAsync, a mailbox reopened after eviction, and CompactAsync) equals the state the relay held live, for any log of accepted updates, including records that reuse an ID with different content
- [x] #2 The two nightly inputs pass through the mailbox, with and without a validation hook, as gating regression tests
- [x] #3 A log already written with overlapping records reloads to the live state with no migration step
- [x] #4 Reload cost on a long uncompacted log is measured and recorded in the task notes against the TASK-13 merge
- [ ] #5 The nightly HOS-MUT tier passes on main
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Regression tests first: the two nightly inputs through the mailbox (Fuzz ServerTierBoundsTests) and in UpdateLogTests (load, catch-up FoldAsync, CompactAsync against the per-record fold); confirmed red.
2. UpdateLog.FoldAsync: decode each record and track the struct clock ranges (Items and GCs, not Skips) per client; records whose ranges are pairwise disjoint are merged and folded in one step as now (TASK-13); a record that overlaps one already in the batch closes the batch, which is folded before it. Each batch folds into the relay in log order, so the result is the per-record fold; an honest log (no overlaps) stays one merge, and a log with k overlapping records costs k+1 merges, not n folds.
3. Keep the per-record fallback that names a bad record; same path for the mailbox rehydration (doc).
4. Cost: ALoad_CostsTheLogsSize stays green; add a variant with overlapping records; record measurements in notes.
5. Server, fuzz and full suites (release gate), API baseline unchanged.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Cause, confirmed with a probe: the hostile record holds a GC at (1,0) beside structs of other clients; live, the relay skips the GC because it holds 'hello' at (1,0); one mergeUpdates over [hello, hostile] keeps the GC (GC + 'ello'), because which reader wins a tie at one clock depends on the other clients in the record. Simpler single-client cases (a GC or a string at (1,0)) happen to merge to the per-record result, so the tests carry both.
Approach (owner chose the per-record fold over store-side trimming): UpdateLog.FoldAsync decodes each record and tracks the clocks its Items and GCs cover per client (ClockRanges, a zero-length struct counts as its clock); records covering disjoint clocks are merged and folded in one step as in TASK-13, and a record covering a clock already in the batch folds the batch first and starts the next. Same path for LoadAsync, catch-up FoldAsync, CompactAsync and the mailbox rehydration. No public API change; logs already written need no migration (the load decides from the records).
Cost (Release, allocation and time per record on load, old merge -> new): in order 1,755 B 3.8 us -> 2,507 B 5.5 us; newest first 1,755 B 4.8 us -> 2,507 B 6.5 us; in order, a resend every 50: 1,770 B -> 2,309 B, flat from 300 to 2,400 records; newest first, a resend every 50: 1,770 B -> 6,442 B at 2,400 (3,037 at 300); newest first, a resend every 10: 1,830 B -> 23,735 B at 2,400 (5,589 at 300). The plain per-record fold was 212,682 B per record at 2,400 newest first (TASK-13). The +43% on an honest log is the extra decode: YSharp.Server sees only YSharp's public API, so it cannot reuse the merge's own decode. Out-of-order logs with many overlapping records grow with the number of overlaps (each one ends a batch); compaction removes them.
Validation: YSHARP_RELEASE_GATE=1 full Debug suite 5,083 total, 0 failed; server tests 149/149 in Release; ServerTierBoundsTests at YSHARP_TIER=Nightly in Release 14/14; Smoke interop 120/120; traceability 42/42; api-surface check unchanged.
Suite changes (abcbb76): UpdateLogTests.ALoad_OfARecordReusingAnId_GivesTheBytesOfFoldingTheRecordsOneAtATime (4 cases; the two nightly ones fail on the old code), ALoad_CostsTheLogsSize_NotItsRecordsTimesTheState now a theory with an in-order-with-resends case; ServerTierBoundsTests.NightlyFinds_ThroughTheMailbox_AreContained (4 cases, failed on the old code).
Docs: server.yaml notes on RLY-2 (the relay keeps what it holds at an id, where mergeUpdates can keep the later struct) and SRV-10.

Nightly run 40536463370 on main (d3651b5, preview.3): the two original finds pass; one new HOS-MUT find (mutation of sim/map-large.json:e) still made the store and the relay disagree after a restart. Cause: not merge order. The V1 update lists client 0 in more than one block and ends with a zero-length GC at clock 0, behind the client's end (3). The update writer groups a client's structs and writes the first clock and the lengths, so the stored V2 record holds Gc(0,3)x0; the relay, prepared from the V1 decode, skipped the GC at clock 0. A reload integrated Gc(0,3)x0, so the restart served other bytes (same content). Fix: DocumentMailbox.SurvivesV2 also returns false when a struct does not start where its client's previous struct ends, so the relay and the hot Doc take the stored form, as they already did for out-of-order delete ranges. Regression: NightlyFinds_ThroughTheMailbox_AreContained gains the find, hooked and not (red before, green after).
<!-- SECTION:NOTES:END -->
