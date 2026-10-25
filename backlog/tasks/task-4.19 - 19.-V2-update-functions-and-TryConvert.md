---
id: TASK-4.19
title: 19. V2 update functions and TryConvert
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:41'
updated_date: '2026-10-25 18:24'
labels:
  - spec
  - 'model:sonnet'
milestone: m-3
dependencies:
  - TASK-4.18
parent_task_id: TASK-4
ordinal: 19000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Merge, diff and state vector on V2 bytes, and `TryConvert` between formats.

**Features:** `V2Functions`
**Owns:** `src/YSharp/Codecs/UpdateFunctions/` (V2 and convert)
**Registry:** V2 update-function and convert `ENC` entries
**Test files:** `Codecs/UpdateFunctionTests.cs` (V2), `YSharp.Fuzz.Tests/Tests/LimitsTests.cs`, `AmplificationTests.cs` (V2)
**Handoff section-7 items:** Fix (with evidence, ADR 0022) the pending-bytes boundary input in `LimitsTests`; the expected limit behaviour stays.
**Suggested model:** sonnet (override freely)

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

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. TryConvert: decode in the source format with the update functions' reader (DecodeForFunctions), write in the target format (Yjs convertUpdateFormat: structs in read order, Skips kept, delete set passed through).
2. MaxPendingBytes (ADR 0005, ERR-10): predict the pending state an update leaves before integrating it, with a dry run of IntegrateStructs and ReadUpdate against a virtual state; reject whole (no transaction) when its V1 size is over the limit.
3. Land Feature.V2Functions; gate the tests that fail only on a later spec's API (standing approval).
4. Check the LimitsTests pending-bytes boundary input against the implementation (handoff item); fix it in a suite: commit only if it is wrong.
5. Debug and Release suites, 0-warning build, fixture drift, API surface.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implementation:
- TryConvert (YUpdate.Convert.cs) reuses DecodeForFunctions and Write; same-format conversion re-encodes, as Yjs does. V2 merge, diff and state vector already dispatched through the V2 codec since spec 18.
- MaxPendingBytes (Integration/Doc.PendingLimit.cs): TryApplyUpdate calls TryIntegrateRemote, which takes the write lock, runs PredictPendingBytes and either releases the lock (LimitExceeded, nothing integrated, no transaction opened) or begins the transaction and integrates. The prediction is the same IntegrateStructs walk with a PendingDryRun (structs placed in a virtual per-client state; Resolve's tail replaced by DryResolve, which finds parents and collected neighbours in the store or among placed structs and never splits), then ReadUpdate's merge, delete-set and retry steps on copies. Size = V1 encoding of the retained pending structs (clients descending, gaps as Skips) plus the pending delete set, 0 when nothing is pending. A Debug.Assert compares every prediction with the state actually left; sabotaging it by one byte fails 12 tests, so the check is live and exact across the suite. IntegrateRemote (no limit) stays for the integration-engine tests.
- Pending-bytes boundary input (handoff item): no fix needed. The waiting update (0101e8070084bf843d00017800, 13 bytes: one origin-bearing item and an empty delete set) is byte for byte the V1 encoding of the pending state it leaves, so the limit at waiting.Length accepts and waiting.Length-1 rejects. Verified by running the test with the GetText post-condition line removed locally: it passed; it failed before MaxPendingBytes was enforced (outcome ok, allowed LimitExceeded).
- Enc25's Hi V1<->V2 conversions pass (verified locally with the Busy half skipped).

Suite changes (ADR 0022, standing gate-correction approval):
- 11fe885 suite: [Requires(Feature.Text)] on LimitsTests AnyDepth_AtTheLimit (theory), AnyDepth_100kDeep, AnyDepth_ObjectsAndMixedNesting, StructCount_Tiny, StructCount_Default, StringBytes_Tiny, StringBytes_Default, UpdateBytes_Tiny, PendingBytes_RetainedStateAtTheLimit; Feature.Text added to Enc25_Convert_GivesTheNativeBytesOfTheOtherFormat. Each failed only on Doc.GetText (the ADR 0013 post-condition and the Busy helper). No open owner decision.

Validation: dotnet build 0 warnings; Debug 1197 passed / 0 failed; Release 1209 passed / 0 failed; npm run check clean (141 fixtures, plan goldens); api-surface unchanged (733 lines).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
V2 update functions complete: TryConvert converts between V1 and V2 as Yjs convertUpdateFormat does, and MaxPendingBytes is enforced: a dry run of the integration walk predicts the retained pending state, and an update over the limit is rejected whole with no transaction. The LimitsTests boundary input was correct as written (evidence in the notes). Feature.V2Functions landed; tests that need Doc.GetText are gated on Text. Verified with Debug and Release suites (0 failed), a live Debug assertion that every prediction is exact, fixture drift and API surface checks.
<!-- SECTION:FINAL_SUMMARY:END -->
