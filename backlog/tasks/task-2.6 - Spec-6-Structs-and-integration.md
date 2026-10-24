---
id: TASK-2.6
title: 'Spec 6: Structs and integration'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 22:01'
updated_date: '2026-10-24 01:14'
labels: []
dependencies:
  - TASK-2.2
  - TASK-2.3
  - TASK-2.4
references:
  - docs/research/structs.md
parent_task_id: TASK-2
ordinal: 33000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 2. Item integrate, GC, skip, pending retries and StructStore behaviour.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Tests and fixtures cover all STR-* and GC-* entries
- [x] #2 Diverge entries have Diverges tests showing the JS behaviour
- [x] #3 Traceability check passes; every owned registry entry is active or pending with a written reason and owner, and no new pending entries were added; owned ledger rows have an outcome other than pending; fixtures regenerate with no drift; gating tier within its time budget; traceability matrix attached to the task
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Node scenarios tools/fixture-gen/scenarios/structs.mjs -> fixtures/structs/* (structs+ds observations) covering the wire:true STR/GC entries; regenerate with no drift.
2. C# test support in tests/YSharp.Tests/Structs: stub guard (skip while Doc is the API stub), StructsProbe (internal Doc.DumpStructs seam), fixture replay engine (OpsInterpreter + local mergeUpdates/insertType steps) comparing doc/state/sv/updates/structs/ds.
3. Unit tests with [Covers] for every STR-* and GC-* entry (public API + dump probe; expectations verified against the Node oracle); [Diverges] none needed (no diverge entries owned).
4. Flip STR-*/GC-* to active in docs/inventory (excluded/pending only with written reason), ratchet pending-high-water.json down.
5. Build+test, traceability matrix, finalize task.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implementation: 28 Node scenarios (tools/fixture-gen/scenarios/structs.mjs) -> fixtures/structs/*.json with structs+ds observations; tests/YSharp.Tests/Structs (13 files, 118 test cases incl. one fixture-replay theory over every structs fixture). Tests compile against the stub and skip via Impl.Run (NotImplementedException) per ADR 0019; they need two internal seams from the implementation, documented in StructsProbe: Doc.DumpStructs() (ADR 0016 structs dump JSON) and Doc.HasPending. Expected values (hex goldens, dumps, strings) were produced by running the same operations on the pinned Yjs. Traceability matrix (TestResults/traceability/traceability-matrix.md, CI artifact): 576 entries, 91 active, 485 pending, 0 violations; spec 6 owns 88 entries: 84 STR/GC + Q20,Q21 active (86), Q22 and Q25 pending with written reasons in quirks-clients.yaml. pending-high-water registry 571 -> 485. Ledger: spec 6 owns no upstream-test rows (ownership follows the upstream file). Deviations: STR-73 re-classified kind diverge (ADR 0005 observer faults) with a [Diverges] test; GC-11 layers drop 'port' (no ledger key to port; covered by SimHarness+StructsSimInternals unit test); fixture.schema.json covers pattern widened to accept Qnn ids (needed for Q20/Q21 fixtures). Validation: dotnet build YSharp.slnx (Debug+Release) 0 warnings; dotnet test --solution YSharp.slnx: 298 total, 167 passed, 131 skipped (stub), 0 failed, ~2.6s; npm run check in tools/fixture-gen: fixtures up to date (45 files).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added Spec 6 struct/integration/GC coverage: 28 Node-generated fixtures (structs+ds observations, pending, Skip, GC, orphan, surrogate, subdoc, client-id cases) and 13 test files (118 cases) covering all 73 STR-* and 11 GC-* entries plus Q20/Q21, with oracle-derived goldens. Tests compile against the API stub and skip until implemented (internal seams Doc.DumpStructs/HasPending documented in StructsProbe). All 84 STR/GC entries are active; Q22 and Q25 stay pending with written reasons; STR-73 is a diverge entry with a [Diverges] test; pending high-water ratcheted 571 -> 485. Verified: traceability gate passes with 0 violations, fixtures regenerate with no drift, full solution build 0 warnings, dotnet test 0 failed (131 skipped on the stub) in ~2.6s.
<!-- SECTION:FINAL_SUMMARY:END -->
