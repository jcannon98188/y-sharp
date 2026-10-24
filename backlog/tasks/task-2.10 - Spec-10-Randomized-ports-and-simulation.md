---
id: TASK-2.10
title: 'Spec 10: Randomized ports and simulation'
status: In Progress
assignee:
  - '@claude'
created_date: '2026-10-23 22:01'
updated_date: '2026-10-24 06:06'
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
- [ ] #1 All testRepeat* tests and testInsertAndDeleteAtRandomPositions ported per ADR 0017 with SIM-01..05 divergences tagged
- [ ] #2 12 golden fixtures and the draw-parity test pass; randomized gating total under about 60 s on Linux Release
- [ ] #3 Traceability check passes; every owned registry entry is active or pending with a written reason and owner, and no new pending entries were added; owned ledger rows have an outcome other than pending; fixtures regenerate with no drift; gating tier within its time budget; traceability matrix attached to the task
- [ ] #4 Implements the C# interpreter side of the 'deliver {schedule}' step (order, duplicate, drop) used by the golden op-log fixtures (Node side exists or is added here), with schema change and [Covers]
- [ ] #5 Takes ownership of Q25 (orphaned: Spec 6 says Spec 7 owns it, Spec 7 says Spec 6): adds a deep-observer recorder step to both interpreters, a fixture, and moves Q25 to active
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
