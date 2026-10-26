---
id: TASK-4.34
title: 34. Permanent user data
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 09:13'
labels:
  - spec
  - 'model:sonnet'
milestone: m-5
dependencies:
  - TASK-4.29
parent_task_id: TASK-4
ordinal: 34000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
`PermanentUserData` mapping, without the JS leak (PUD-05, ADR 0018).

**Features:** `UserData`
**Owns:** `src/YSharp/DocFeatures/UserData/`
**Registry:** `PUD` entries
**Test files:** `DocFeatures/PermanentUserDataTests.cs`, PUD cases of `Codecs/UpstreamPortTests.cs`
**Suggested model:** sonnet (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [x] #2 This spec's features are added to `Features.Landed`
- [x] #3 The traceability gate passes and no pending registry entry is added
- [x] #4 `DocFeatureFixtureTests` pass once specs 29 to 34 have landed
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
Implementation: PermanentUserData.cs ports utils/PermanentUserData.js. It has a clients map, a per-user merged delete set (insertion order, for getUserByDeletedId) and initUser observers on ds and ids. setUserMapping creates the user if needed, pushes the client id, registers an overwrite port and registers an afterTransaction recorder. Both of the last two run through the Defer seam, as setTimeout does in JS. Lookups take the document read lock.
Suite changes: none.
Verification: Debug and Release full suites are green (8780 total, 0 failed). DocFeatureFixtureTests: all 37 pass with no skips (AC #4). No skip reason names UserData. 0 warnings. npm check is clean.
Owner sign-off:
- The filter and the transaction's local flag and delete set are read synchronously in AfterTransaction. JS reads them later in a timer, from the ended transaction.
- The default Defer is the thread pool, standing in for setTimeout(0).
- A users entry that is not a map is replaced on setUserMapping (JS would throw).
- A deleted user key does not crash the overwrite port (PUD-07; JS throws).
- ds entries that are not bytes or do not decode are ignored (JS throws in initUser).
- clientId above 2^53-1 throws ArgumentOutOfRangeException.
- Deferred actions are skipped once the doc is destroyed, and their faults are reported through ObserverFaulted.
- GetUserByClientId returns null for an empty description, as JS's || null does.
- Every write is its own transaction, as in JS.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
PermanentUserData landed per Yjs utils/PermanentUserData.js, with a Defer seam for the setTimeout calls. All PUD tests and all 37 doc-feature fixtures pass. Debug and Release are green. Owner sign-offs are in the notes.
<!-- SECTION:FINAL_SUMMARY:END -->
