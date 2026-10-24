---
id: TASK-2.9
title: 'Spec 9: Awareness, sync and auth'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 22:01'
updated_date: '2026-10-24 05:43'
labels: []
dependencies:
  - TASK-2.2
  - TASK-2.3
  - TASK-2.4
references:
  - docs/adr/0014-awareness-and-protocol-test-scope.md
  - docs/research/y-protocols.md
parent_task_id: TASK-2
ordinal: 36000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 2. All three y-protocols modules; providers out of scope.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Tests and oracle-generated goldens cover AW-* and SY-*
- [x] #2 TimeProvider seam used for timeouts; two-peer convergence harness and structural-equality case table exist
- [x] #3 Traceability check passes; every owned registry entry is active or pending with a written reason and owner, and no new pending entries were added; owned ledger rows have an outcome other than pending; fixtures regenerate with no drift; gating tier within its time budget; traceability matrix attached to the task
- [x] #4 Registers, on both the Node generator and the C# interpreter, the protocol step kinds of ADR 0016: sync.step1/step2/update and awareness.setLocal/applyUpdate/removeStates/encode; each with schema change and [Covers]
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Node: awareness/sync/auth step kinds with virtual timers, schema, scenarios and generated fixtures. 2. C#: protocol step registry, fixture replay, unit/port/diverge tests, two-peer harness, equality case table. 3. Activate AW/SY/AU/PR/Q32 entries, ledger, pending mark. 4. Verify, merge integration tip.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Evidence: dotnet build 0 errors; dotnet test --solution: 1127 total, 0 failed, 176 passed, 951 skipped (stub, ADR 0019); traceability gate passes; npm run check: fixtures up to date (116 files). 86 owned entries (AW-1..52, SY-1..20, AU-1..7, PR-1..6, Q32) active, 0 new pending; testAwareness ported; pending mark ratcheted to 72/49. Matrix: docs/inventory/matrices/spec-9-traceability.md. API gaps: stub gained TryEncodeUpdate(+states) and YSync.TryReadMessage errorHandler; EncodeUpdate empty-list=all conflicted with AW-31; ids are uint (JS 53-bit, AW-34); GetStates snapshot semantics (AW-13) unspecified; AU-5 null handler assumed ArgumentNullException; SY-19 error reporting channel assumed Status/handler. Shared edits: oracle/steps/observe (virtual timers, results extras), Spec 7 StepKindTests filter.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added awareness, sync and auth coverage: Node step kinds with virtual timers and 25 oracle-generated fixtures, C# interpreter and fixture replay, AW/SY/AU/PR/Q32 tests (unit, port, diverge), two-peer harness and structural-equality case table. Verified by build, test (0 failed), traceability gate and npm run check.
<!-- SECTION:FINAL_SUMMARY:END -->
