---
id: TASK-4.36
title: 36. Sync and auth
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 09:35'
labels:
  - spec
  - 'model:sonnet'
milestone: m-6
dependencies:
  - TASK-4.17
parent_task_id: TASK-4
ordinal: 36000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
y-protocols sync steps and auth messages.

**Features:** `Sync`
**Owns:** `src/YSharp/Protocols/Sync/`, `src/YSharp/Protocols/Auth/`
**Registry:** `SY`, `AU`, `PR` entries, Q32
**Test files:** `Protocols/SyncTests.cs`, `AuthAndPeerTests.cs`, `ProtocolDocsTests.cs`, `ProtocolStepKindTests.cs`, `ProtocolFixtureTests.cs`
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implemented YSync (sync.js) and YAuth (auth.js) and landed Feature.Sync.
- Step1 validates the remote state vector (StateVector.TryDecode) before writing the Step2 reply, so a bad vector is an error status with nothing written (JS has already written the reply's type byte when encodeStateAsUpdate throws). An empty Step1 payload is refused, as in JS.
- Step2/Update: a payload that cannot be read or applied calls errorHandler (as JS) and is also the returned status, with the result naming the type and the failure in Apply; the hostile corpus and truncation suites require the status, the fixture recorder already assumed it (Threw = !Ok && !handlerCalled).
- Input advances past what was read on failure too, matching the JS decoder positions recorded in fixtures/sync/read-errors.json.
Verification: Debug and Release suites green; skip audit shows no skip naming Sync (remaining Concurrency, Fuzz, Sim); interop smoke tier with Node green; npm run check clean.
Gate correction (pre-approved): CorpusReplayTests.Corpus_EveryStoredFile_LeavesTheDocumentUnfaultedAndUsable now also requires Fuzz, since the whole corpus includes the Fuzz-gated HOS-REF and HOS-AMP cases (over-allocation on 5000-structs cases and parent-is-self are TASK-4.42 material).
Suite changes (commit d91ac19):
- ProtocolFixtureTests: a result without "threw" (writer results) counts as not thrown.
- ProtocolSteps sync.read: records the type when the error handler ran.
- ProtocolStepKindTests: removeStates test fed an awareness entry with an empty JSON string (invalid in JS too), now {}; origin read null-safe.
- SimHarness.Compare: accepts one user, as upstream (users: 1 ports such as testSetAndClearOfMapProperties).
- CorpusReplayTests gate as above.
Owner sign-off:
- Bad Step2/Update payloads return an error status in addition to calling errorHandler (JS returns normally after the handler).
- A bad Step1 state vector writes no partial reply (JS leaves the type byte 0x01 in the encoder).
- Observer faults during a Step2/Update apply are not sent to errorHandler; they go to doc.ObserverFaulted and ApplyResult.ObserverFaults (JS routes observer exceptions to the handler).
- Invalid UTF-8 in an auth reason is an error result (JS substitutes U+FFFD); the reason length is capped by doc.Limits.MaxStringBytes.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
y-protocols sync (Step1/Step2/Update writers and readSyncMessage) and auth (permission denied) ported over the V1 update functions, with JS-matching byte positions and errors as statuses (ADR 0005). Feature.Sync landed; all suites green in Debug and Release plus the interop smoke tier; suite fixes and sign-offs recorded in the notes.
<!-- SECTION:FINAL_SUMMARY:END -->
