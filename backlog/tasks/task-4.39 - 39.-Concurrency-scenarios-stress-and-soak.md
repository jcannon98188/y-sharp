---
id: TASK-4.39
title: '39. Concurrency scenarios, stress and soak'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 10:35'
labels:
  - spec
  - 'model:opus'
milestone: m-7
dependencies:
  - TASK-4.28
  - TASK-4.34
  - TASK-4.37
parent_task_id: TASK-4
ordinal: 39000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The CC contract (ADR 0006) under the hand-rolled scheduler, stress and soak, plus the Debug-only CC-18 lock-order checker.

**Features:** `Concurrency`
**Owns:** lock-order checker in `src/YSharp/Transactions/` (Debug only), concurrency test fixes
**Registry:** `CC` entries
**Test files:** `Concurrency/*`, `YSharp.Fuzz.Tests/Concurrency/SoakTests.cs`
**Suggested model:** opus (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [x] #2 This spec's features are added to `Features.Landed`
- [x] #3 The traceability gate passes and no pending registry entry is added
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 `dotnet build YSharp.slnx` has 0 warnings
- [x] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [x] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [x] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
From TASK-4.33 (subdocs): the Debug lock-order checker that rejects taking a subdocument's lock and then its parent's (CC-18) is not in src. Spec 33 keeps the order by construction (Load and Destroy reach the parent with the subdocument's lock released, see Doc.Subdocs.cs). The checker and its tests (gated on Feature.Concurrency) land here.

Results: Debug 4424 tests, 0 failed, 773 skipped (none for Concurrency not landed); Release 4424, 0 failed, 749 skipped; nightly soak (YSHARP_TIER=Nightly, Release) 6 tests: 5 passed, 0 failed, 1 skipped (subdoc-abba needs the Debug checker); npm run check clean.

Library fixes found by landing Concurrency:
- CC-18: DocLock has a Debug-only lock-order checker. Requesting a document's lock while the thread holds a lock on one of its subdocuments throws YConcurrencyException instead of deadlocking (the ABBA scenario and a ComponentTests timeout). TryEnterWrite is not checked, since it never waits. ADR 0006 calls the checker 'injectable'; it is not. It is always on in Debug builds and compiled out of Release. The behaviour matches the ADR's intent, so this is a wording note, not a sign-off.
- CC-4: YArray index lookups in a read transaction stamped and moved the shared search markers, so concurrent readers raced and could return the wrong element. Without the write lock, Get now uses SearchMarkers.Peek, which is read-only. Regression test: IndexLookups_InAReadTransaction_LeaveTheSearchMarkersAsTheyWere.

Suite changes (commit 22816a7, ADR 0022):
1. LockModelTests TryTransact upgrade test runs TryTransact on the reading thread; it ran on another thread, which is plain contention, not an upgrade.
2. CooperativeScheduler.WaitUntilBlockedAt: the writer-preference scenario waits until each thread is blocked at a named point, removing a race in the scenario.
3. The exhaustive DFS runs two-thread variants (UpgradeIsRejectedPair, DestroyVsWritePair); the three-thread versions run under the random scheduler. The three-thread versions passed 20000+ schedules without exhausting.
4. StressHarness implicit mode reads lengths once and counts a racing index miss as 'raced', which is valid for implicit transactions.

Release skips are by design: the nightly soak tests, the Debug-only checker test, and subdoc-abba (DebugOnly).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Concurrency is landed. The lock model, scheduler scenarios (exhaustive DFS for the two-thread cases, random for the rest), stress and nightly soak all pass. Landing it surfaced two library bugs, both fixed: a parent/subdoc lock-order deadlock, now refused by a Debug checker (CC-18), and read transactions mutating the YArray search markers (CC-4), now read-only via SearchMarkers.Peek. Debug and Release suites are green; the soak run is green.
<!-- SECTION:FINAL_SUMMARY:END -->
