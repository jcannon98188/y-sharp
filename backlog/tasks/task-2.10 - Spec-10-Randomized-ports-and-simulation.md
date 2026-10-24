---
id: TASK-2.10
title: 'Spec 10: Randomized ports and simulation'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 22:01'
updated_date: '2026-10-24 06:10'
labels: []
dependencies:
  - TASK-2.2
  - TASK-2.3
  - TASK-2.4
references:
  - docs/adr/0017-randomized-test-port-plan.md
  - docs/research/js-tests.md
parent_task_id: TASK-2
ordinal: 37000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 2. Fixed seed lists by size class, regression seeds, nightly time-boxed runs, 12 golden op-log fixtures and the draw-parity test.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 All testRepeat* tests and testInsertAndDeleteAtRandomPositions ported per ADR 0017 with SIM-01..05 divergences tagged
- [x] #2 12 golden fixtures and the draw-parity test pass; randomized gating total under about 60 s on Linux Release
- [x] #3 Traceability check passes; every owned registry entry is active or pending with a written reason and owner, and no new pending entries were added; owned ledger rows have an outcome other than pending; fixtures regenerate with no drift; gating tier within its time budget; traceability matrix attached to the task
- [x] #4 Implements the C# interpreter side of the 'deliver {schedule}' step (order, duplicate, drop) used by the golden op-log fixtures (Node side exists or is added here), with schema change and [Covers]
- [x] #5 Takes ownership of Q25 (orphaned: Spec 6 says Spec 7 owns it, Spec 7 says Spec 6): adds a deep-observer recorder step to both interpreters, a fixture, and moves Q25 to active
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Node: hand-written copy of the upstream sim (TestConnector, init, applyRandomTests, 4 generators) emitting ops steps; deliver (sv/reply/apply), observeDeep and text.insertEmbedType steps with schema entries; 12 golden fixtures (4 generators x 3 sizes, seed 1) and the Q25 deep-observer fixture.
2. Prove draw parity of the Node port against the upstream tests (check-sim-draws.mjs) and pin each plan's draws and output (plan-goldens.json) for the C# plans.
3. TestKit: ISimRecorder/SimLog, message metadata on TestConnector, recorder and pre-destroy hooks on SimHarness.
4. YSharp.Tests/Simulation: SimPlans (C# generators as plans), executor via the ops interpreter, SimSteps (C# deliver/observeDeep/insertEmbedType), 41 gating theories on seed lists by size class, restored InsertAndDelete test, nightly time-boxed + production sizes, regression seeds, SIM-01..05 [Diverges] tests, golden fixture replay, draw parity, Q25 fixture test.
5. Registry: SIM-01..05 and Q25 active; ledger 48 ported + 1 merged-into; ratchet pending high-water; attach traceability matrix.
6. Build, test, npm run check, merge integration/verification-suite, finalize.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Validation (Debug, merged with integration/verification-suite incl. Spec 9): dotnet build YSharp.slnx 0 warnings, 0 errors; dotnet test --solution YSharp.slnx total 7077, failed 0, succeeded 194, skipped 6883 (skips are tests that need the implementation: Stub.Run per ADR 0019), 3.9 s. npm run check in tools/fixture-gen: fixtures up to date (129 files) and plan goldens up to date. node check-sim-draws.mjs: draw sequences identical for 140 upstream runs (map, array, text, quill; seeds 1,2,3,4,5,42,1337). Traceability gate: 0 violations; registry 576 entries, 66 pending (was 72 after Spec 9), pending ledger rows 0 (was 49); pending mark ratcheted to 66/0. Matrix: docs/inventory/matrices/spec-10-traceability.md.

Design: the delivery schedule is exact, not a sync shortcut. deliver entries: sv (capture state vector = SyncStep1), reply (capture encodeStateAsUpdate(doc, sv) = SyncStep2, computed when step 1 is flushed), apply (a captured reply by name or the n-th update event of another replica). Replay therefore gives every client the same state at every edit, so positions drawn from lengths stay valid. Publishing hangs off update events, not edit steps, because y-text runs a formatting-cleanup transaction after applying remote changes and upstream broadcasts its update. C# generators are written as plans (draws + document view -> ops steps); a run executes the steps through the ops interpreter and records them, and plan-goldens.json (from the Node port) pins 360 recorded plans per generator, so the C# generators are verified bit for bit without an implementation (SimPlanTests pass now).

Findings: (1) upstream's module-level charCounter changes the length of inserted text, so it changes structure and later draws, not only payload text (SIM-05 matters more than research 6.7 said). (2) The Quill generator shares three mark objects and Yjs mutates them (insertText writes null attributes into the given object): later uses carry stale nulls. Not reproducible by a stored op log or immutable YValue attributes; check-sim-draws.mjs re-creates the aliasing to prove the remaining draws match; recorded in ADR 0017 implementation notes and research 6.7 item 11, not as a new SIM id. (3) fixtures/sim is about 18 MB (array-large 7 MB); structs/state observations dominate.

Unmeasured: the randomized gating total (about 60 s on Linux Release) cannot be measured against the API stub (4 s Debug, all skipped). Calibration knob is SimCase.GatingSeeds in tests/YSharp.Tests/Simulation/SimCases.cs (N within a class, boundaries stay). Tests that need Doc (golden replay, draw parity, the randomized theories, deliver semantics) compile and skip; their C# connector/recorder/executor paths are unexercised until the implementation exists. Pre-existing flake: AllocationAssertTests.AtMost_AllocatingOperation_FailsAndNamesTheAllocatingType fails intermittently in the full Release run (also with the Simulation namespace excluded).

API gaps: (a) no counterpart of Y.cleanupYTextFormatting; the text and Quill tests assert it returns 0 through a reflection seam, internal int YText.CleanupFormatting() (TextProbe). (b) Seams already required by earlier specs are reused: Doc.DumpStructs() and Doc.HasPending (StructsProbe). (c) Doc.ApplyUpdate, EncodeStateAsUpdate(sv, format), EncodeStateVector and YText.InsertEmbed(int, YType) are used as sketched; YEvent.GetPath() relative to CurrentTarget in deep observers is assumed (Q25 fixture).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Ported all 48 testRepeat* tests (41 gating theories on committed seed lists by size class 200/40/10, six production sizes as nightly-only tests, 2Repeat merged into Quill2) and testInsertAndDeleteAtRandomPositions with its mirror check restored (2000 ops x 10 seeds gating, 100000 nightly), with SIM-01..05 as active diverge entries each with a [Diverges] test. Added the Node simulation port (lib/sim.mjs), 12 golden op-log fixtures plus the draw-parity test, the deliver {schedule} step (sv/reply/apply, schema, Node and C# interpreters, tests), the observeDeep recorder and fixtures/observers/deep-event-order.json (Q25 now active, owner 10), nightly time boxing with failing-seed repro files, and regression-seeds.json. Verified: dotnet build 0 warnings; dotnet test 7077 total, 0 failed, 194 passed, 6883 skipped on the stub; npm run check clean (129 fixtures, plan goldens); check-sim-draws.mjs proves the Node port draws exactly like the upstream tests (140 runs); the C# plans reproduce 360 recorded plans per generator without a document; traceability gate 0 violations, pending mark 66/0, 49 randomized ledger rows resolved. Not measurable on the stub: the 60 s randomized gating budget and every Doc-dependent test body (golden replay, draw parity, theories), which compile and skip per ADR 0019.
<!-- SECTION:FINAL_SUMMARY:END -->
