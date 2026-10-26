---
id: TASK-4.41
title: '41. Differential II: sync and awareness'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 12:53'
labels:
  - spec
  - 'model:opus'
milestone: m-7
dependencies:
  - TASK-4.37
  - TASK-4.40
parent_task_id: TASK-4
ordinal: 41000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Protocol coverage for the live differential.

**Owns:** `tests/YSharp.Interop.Tests` protocol generators
**Registry:** `DIFF` entries
**Handoff section-7 items:** Build the `sync.*` and awareness generators (`SharpSide.Extensions` hook).
**Suggested model:** opus (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The traceability gate passes and no pending registry entry is added
- [x] #2 Protocol generators run in smoke and nightly; every mismatch fixed or raised as an owner decision
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
Implementation:
- ProtocolPlan (Generation/ProtocolPlan.cs) generates sync, auth and awareness exchanges over 2-3 replicas: hostile sync bytes, unknown/large client ids (small, 2^32+r, 2^53-1-r), hand-built awareness batches (bad JSON, truncation, trailing bytes), modify modes, ticks, destroy. Taint rules keep a thrown read's partial reply, auth messages and anything appended to them from being used as sources.
- The interop side links the shared tests/YSharp.Tests/Protocols/ProtocolSteps.cs and registers its steps directly in SharpSide.CreateRegistry (not via the Extensions hook the handoff mentions: the steps already exist as one shared file). Results carry api kind, bytes, and per kind type/threw/handlerCalled/consumed; a new 'awareness' observation dumps client id, sorted states and meta with virtual times relative to the first awareness.new (assumes awareness.new precedes any tick, which ToScript guarantees).
- Properties (smoke 80/120/60 cases, nightly scaled): Differential_SyncExchanges_ProduceEqualMessagesResultsAndState, Differential_Awareness_ProducesEqualUpdatesStatesMetaAndEvents, AwarenessBatchesFailingPartway_AreExactlyTheRecordedAtomicDivergence (DIFF-05/AW-51 normaliser: the oracle probe finds rejected batches, they are rewritten to fail on their first entry, y-sharp must equal that run and the two oracle runs must agree on every document observation).
- Findings fixed: (1) library: Awareness.Dispose kept its handlers; y-protocols' destroy ends with Observable.destroy, so later state changes raised events. Now dropped (AW-22). (2) suite: setField did not spread strings/arrays as JS {...state} does. (3) suite: named apply/modify with a missing input recorded nothing; Node records a throw.
- Nightly: ProtocolDifferentialTests green twice in Release; DifferentialTests (all 13) green.

Suite changes (ADR 0022), commit 73822ed:
- ProtocolSteps: Register(registry), Kind on every result, awareness events into the shared ordered event log, missing named input = threw, setField spread semantics (test: AwarenessWireTests.Modify_WithTheSpreadStepHelper_SpreadsStringsAndArraysToIndexKeys_AsJsDoes).
- ComparatorTests covers the awareness observation; HarnessSelfTests runs generated protocol scripts on the real oracle.

Owner sign-off:
- Awareness.Dispose now drops Change/Update/Destroyed handlers after the final events, matching y-protocols (AW-22 is a match entry). Behaviour change for callers that relied on handlers surviving dispose.
- AW-51 normaliser approach (oracle rewrite of rejected batches) as the DIFF-05 assertion of the recorded atomic-batch divergence (ADR 0005).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Differential II lands: generated sync, auth and awareness scripts run on y-protocols and on y-sharp with every result, an awareness dump and the event log compared, plus a DIFF-05 property pinning the AW-51 atomic-batch divergence. It found one library bug (Awareness.Dispose kept handlers, AW-22), fixed, and two step-interpreter gaps, fixed as suite changes. Suites green Debug and Release with 0 warnings, smoke and nightly differentials green, fixtures clean. Owner sign-offs recorded for the Dispose behaviour change and the AW-51 normaliser.
<!-- SECTION:FINAL_SUMMARY:END -->
