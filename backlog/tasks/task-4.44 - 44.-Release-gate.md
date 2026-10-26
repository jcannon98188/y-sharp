---
id: TASK-4.44
title: 44. Release gate
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 20:31'
labels:
  - spec
  - 'model:sonnet'
milestone: m-7
dependencies:
  - TASK-4.38
  - TASK-4.39
  - TASK-4.41
  - TASK-4.42
  - TASK-4.43
parent_task_id: TASK-4
ordinal: 44000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The plan ends here: the handoff's progress measure (skip count) is zero and every gate is green.

**Owns:** no library code; final traceability matrix
**Suggested model:** sonnet (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every feature is in `Features.Landed`; zero tests skip for a missing feature
- [x] #2 Zero pending registry entries and ledger rows; `YSHARP_RELEASE_GATE=1` passes
- [x] #3 All `BUD-*` entries active with recorded values; slow memory tier green
- [x] #4 Every M7 spec is Done and the M7 milestone is closed
- [x] #5 The traceability gate passes and no pending registry entry is added
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 `dotnet build YSharp.slnx` has 0 warnings
- [x] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [x] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [x] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Activate the last pending budgets (BUD-13 Yjs bytes-per-item comparison, BUD-14) with measured values; ratchet docs/inventory/pending-high-water.json to 0.
2. Make the feature gate a release gate: Features.Landed must equal the Feature enum, each once (drop the duplicate Values).
3. Generate the release traceability matrix (docs/inventory/matrices/release-traceability.md).
4. Fix the nightly Bytes-per-item table so it measures the same workloads as the Yjs script, each in its own process.
5. Run the full suite in Debug and Release with YSHARP_RELEASE_GATE=1, the fixture drift check, and the Nightly workflow (tests, benchmarks against the stored baseline, bytes table); fix every real finding (ADR 0006).
6. Close milestone m-7.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Budgets: BUD-13 and BUD-14 active. Local Release bytes per item: text 98.11, array 8.08 (Yjs 8.31), map 156.86, the same as MemoryTierTests. The pending high-water mark went from 9 to 0 (registry).

Feature gate: Features.Landed listed Values twice. Removed it; the new test Features_Landed_IsEveryFeature_EachOnce holds the gate.

Release traceability matrix: docs/inventory/matrices/release-traceability.md (591 entries: 587 active, 0 pending, 4 excluded).

Bench table fix: the nightly Bytes-per-item table used a different text workload from tools/measure.mjs and measured every workload in one process (map 76 vs 157 bytes when measured alone). BytesPerItemTable now runs each YjsWorkloads entry in a child process (table-one <workload>), and YjsComparisonBenchmarks.TextInserts uses the same fragmented-insert workload.

Nightly find (run 40372636845): the live differential's mutated-updates property found that an inserted byte gives a format item an origin inside array a, so the item integrates into the array (Yjs takes the parent from the origin). Building the array's event delta threw YInternalException (a Format item has no content to read), and the observer fault dropped the array's insert event, which the oracle fired. Yjs's list delta concatenates content.getContent(), which is [] for ContentFormat, while the item's length still counts toward retain and delete. TypeList.AddContent now adds nothing for Format and Deleted content. No divergence: y-sharp now matches the oracle, and the saved repro replays green. Regression coverage: EventTests.ListDelta_FormatItemInAnArray_InsertsNothingForIt_AsYjs (the oracle's deltas, covers V-6 and A-E2) and the Smoke live test MutatedFormatInArrayTests (DIFF-05, V-6).

Suite changes (ADR 0022):
- 8d9460c suite: activate BUD-13 and BUD-14, ratchet the pending mark to zero, release traceability matrix
- 5830374 suite: a format item in an array inserts nothing in the array's event delta, the Nightly differential find

Validation:
- Debug and Release with YSHARP_RELEASE_GATE=1: 4466 total, 0 failed, 0 warnings. The skips are all tier or config gates (Release-only allocation, spike and memory tests; Smoke and Nightly tiers; YSHARP_REPLAY); none is for a missing feature.
- Interop in the Smoke tier with Node required: 117 passed, 1 skipped (YSHARP_REPLAY).
- npm run check: fixtures and plan goldens up to date.

Owner sign-off: the BUD-13 array-push margin (8.08 vs Yjs 8.31 bytes per item, about 3% headroom) was approved by the owner on 2026-10-26. SIM-15 (from TASK-4.43) stays logged for owner sign-off.

Nightly 40379583357 (head 894c911) is green: Nightly tier tests, benchmarks, comparison with the stored baseline (0 regressions, 12 unchanged; TextInserts reads 0.21x only because d3fdbab changed its workload to the Yjs script's, so the baseline measured different work), and the bytes-per-item table (y-sharp / Yjs: text-insert 98.10 / 194.77, array-push 8.08 / 8.31, map-set 156.89 / 312.26).

Open: DoD 4 and AC 4 wait on the owner's SIM-15 sign-off (asked 2026-10-26). After that, set Done and close m-7.

CI flake fixed (gating windows-latest Release, run 40386696436): NodeClientTests.Request_ConcurrentRequests_AreMatchedById timed out ('Node did not answer echo within 10000 ms'), as Request_Timeout_KillsAndRestartsTheChild did twice earlier today, always on Windows. Cause: on Windows a child's stdio pipes are synchronous handles, so NodeClient's ReadLineAsync loops blocked pool threads, and a starved pool held the response; the Task.Delay timer's high-priority callback also beat the answer's continuation in WhenAny. Reproduced locally with 20 concurrent echoes under a starved pool (10 s timeout each run, 26 ms unstarved). Fix: stdout and stderr are read on dedicated background threads, the handshake comes through the stdout reader, a read error fails the pending requests, and the timeout path checks tcs.Task.IsCompleted. The probe now passes every run.
Suite change: bd91f3a suite: NodeClient reads the child's pipes on dedicated threads and asks whether the answer came.

Owner sign-off (2026-10-26):
- SIM-15: cleanup may remove the markers Yjs leaves, as long as the content is unchanged and a second cleanup removes nothing. Registry note updated (d2adfc0).
- BUD-04, BUD-05 and BUD-07: the bounded limits are approved for now. TASK-8 (after TASK-5 and all its subtasks) makes one more attempt at zero allocation; if zero stays out of reach, the bounded limits stand.
- BUD-12: k = 64 with 16% headroom, approved.
- BUD-13 array-push margin (8.08 vs Yjs 8.31): approved, recorded above.
No open owner decision remains: no file says pending owner sign-off any more.
Suite changes: d2adfc0 suite: SIM-15 owner sign-off recorded; 2537a89 suite: owner sign-offs for BUD-04, BUD-05, BUD-07 and BUD-12.
CI on PR 1 at bd91f3a and 18fd107: gating (ubuntu Debug/Release, macos, windows), smoke and drift all pass.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Release gate reached. Every feature is in Features.Landed (the duplicate Values is removed, and a test holds the gate). BUD-13 and BUD-14 are active with measured values, the pending high-water mark is ratcheted to 0, and the release traceability matrix is generated (591 entries, 0 pending). The nightly bytes-per-item table now measures the Yjs script's workloads, each in its own process.

Two real bugs from verification are fixed with regression tests:
- An array event delta threw on a format item that a foreign update put in an array, which dropped the event; it now inserts nothing for that item, as Yjs's getContent does.
- Windows CI flaked because NodeClient lost responses under a starved thread pool; it now reads the pipes on dedicated threads and checks whether the answer came.

Every owner decision is signed off: SIM-15, BUD-04/05/07 (bounded; TASK-8 tries for zero after TASK-5), BUD-12 and the BUD-13 margin.

Verified with:
- Debug and Release, YSHARP_RELEASE_GATE=1: 4466 tests, 0 failed, 0 warnings, no skip for a missing feature.
- Interop Smoke tier with Node required: green.
- npm run check: clean.
- Nightly 40379583357: green, with 0 benchmark regressions against the stored baseline.
- PR CI: green on Linux, macOS and Windows.
<!-- SECTION:FINAL_SUMMARY:END -->
