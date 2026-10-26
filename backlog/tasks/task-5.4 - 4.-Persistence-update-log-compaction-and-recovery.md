---
id: TASK-5.4
title: '4. Persistence: update log, compaction and recovery'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-25 17:38'
updated_date: '2026-10-26 21:07'
labels:
  - server
  - 'model:opus'
dependencies:
  - TASK-5.2
parent_task_id: TASK-5
ordinal: 57000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A server must survive restarts without keeping every document in memory. The design is an append-only update log per document, periodic compaction by merging updates (no Doc load, stored as V2), and recovery on load. Crash consistency is the hard part: a crash between append and compaction must not lose or duplicate state. **Suggested model:** opus (override freely).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A storage interface from the ADR in TASK-5.1 plus an in-memory implementation used by tests
- [x] #2 Updates are appended durably before they are acknowledged or fanned out, and an optional sequence number identifies each
- [x] #3 Compaction merges the log via the relay tier into one V2 update and swaps it in atomically
- [x] #4 Recovery after a simulated crash at every step of append and compaction yields a state equal to the pre-crash state, with no loss and no double apply
- [x] #5 Replaying the same log twice is idempotent
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. New project src/YSharp.Server (ADR 0024 SRV-2, SRV-3) with tests/YSharp.Server.Tests; add both to the solution and the test project to the traceability scan.
2. IUpdateStore per SRV-10 (AppendAsync, ReadSinceAsync, CompactAsync, DeleteAsync), with the partial-write and corrupted-tail contract in its docs, and InMemoryUpdateStore (per-document lock).
3. UpdateLog: validate and convert to V2 on append; LoadAsync/FoldAsync recover by folding into a YRelay; CompactAsync merges through the relay and swaps atomically.
4. Tests: numbering and V2 storage, refusal, compaction with later appends, an append racing a compaction, a crash at every step of append and compaction (several occurrences each) with resend, idempotent replay, a corrupt record failing the load.
5. Inventory SRV-10, SRV-11; full suite.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Design: UpdateLog validates with YUpdate.TryConvert(format -> V2) against Limits, so a record is always a valid V2 update. Records are read back under StoredLimits (Limits without the update-size and struct-count caps), because a snapshot merges the whole history and can exceed one incoming update's limits; depth and string caps still apply.
Crash model: the store is atomic per call, so the crash points are the boundaries around each call (before/after an append's write, before/after the compaction swap, midway through a read). A restart is a new UpdateLog over the surviving store; the client resends what was not acknowledged. Pre-crash state = acknowledged updates, plus the in-flight one when the store had recorded it.
A record that fails to decode fails LoadAsync with InvalidDataException (the store contract says torn records read as absent, so a bad record is a broken store, not something to skip).
Deleted documents restart at sequence 1 (no tombstones kept); the IUpdateStore docs say a reader must not resume a deleted document by sequence.
Sabotage check: a compaction that clears every record (not just those it covered) is caught by AnAppendThatRacesACompaction_SurvivesIt.
Gate correction (pre-approved): TestScanner.FindSolutionTestAssemblies now also scans YSharp.Server.Tests.
Suite changes (ADR 0022): new tests/YSharp.Server.Tests (UpdateLogTests, CrashingStore); TestScanner scans the new project; SRV-10 and SRV-11 added to docs/inventory/server.yaml and id-lock.txt. No existing test changed.
Validation: YSharp.Server.Tests 30/30; full suite with YSHARP_RELEASE_GATE=1 (Debug) 4553 total, 0 failed, 776 skipped (Node tiers).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added the YSharp.Server project with IUpdateStore (SRV-10 contract, including partial writes and corrupted tails), InMemoryUpdateStore and UpdateLog: validated V2 appends with sequence numbers, recovery by folding into a YRelay, and atomic compaction through the relay tier. Verified with crash injection at every step of append, compaction and read, a compaction racing appends, idempotent replay and Doc parity; full suite green with the release gate.
<!-- SECTION:FINAL_SUMMARY:END -->
