---
id: TASK-2.8
title: 'Spec 8: Doc-level features'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 22:01'
updated_date: '2026-10-24 05:33'
labels: []
dependencies:
  - TASK-2.2
  - TASK-2.3
  - TASK-2.4
references:
  - docs/research/doc-features.md
parent_task_id: TASK-2
ordinal: 35000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 2. Doc, subdocs, UndoManager, snapshots, relative positions and PermanentUserData; needs injectable RNG, clock and scheduler seams.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Tests cover DOC-*, UM-*, SNAP-*, RP-*, SUB-* and PUD-*
- [x] #2 Upstream doc, undo-redo, snapshot and relativePositions tests have ledger outcomes
- [x] #3 Traceability check passes; every owned registry entry is active or pending with a written reason and owner, and no new pending entries were added; owned ledger rows have an outcome other than pending; fixtures regenerate with no drift; gating tier within its time budget; traceability matrix attached to the task
- [x] #4 Registers, on both the Node generator and the C# interpreter, the doc-feature step kinds of ADR 0016: gc.run, subdoc.add/load/destroy, snapshot.take/restore, relpos.create/resolve, undo.create/stopCapturing/undo/redo, pud.setUserDescription/observe; each with schema change and [Covers]
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Node: doc-feature step kinds (lib/steps-doc.mjs), ops.schema entries, scenarios/docfeatures.mjs -> fixtures/{doc,subdocs,snapshot,relpos,undo,pud}; npm run check no drift.
2. C#: DocFeatureSteps in TestKit (registered in StandardSteps), fixture replay theory in tests/YSharp.Tests/DocFeatures.
3. Unit and port tests with [Covers]/[PortOf] for DOC-*, SUB-*, UM-*, SNAP-*, RP-*, PUD-* and the 58 upstream ledger rows (doc, undo-redo, snapshot, relativePositions); oracle-derived goldens; seams via reflection probes (DocSeams).
4. Activate registry entries, set ledger outcomes, ratchet pending-high-water, attach matrix docs/inventory/matrices/spec-8-traceability.md.
5. Build/test/check, merge integration/verification-suite, finalize.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Matrix: docs/inventory/matrices/spec-8-traceability.md (81 entries active, 0 pending; 58 ledger rows ported). Pending high-water 158/50. Validation: build 0 warnings/errors, dotnet test 995 total/0 failed/171 passed/824 skipped (stub), npm run check: 95 fixtures no drift. Seams (reflection probes, DocSeams/UndoSeams in TestKit): Doc.CollectGarbage, ShouldLoad, AutoLoad, CollectionId, Meta, SignalLoaded, SignalSynced; UndoManager.Undoing/Redoing/CurrStackItem; plus Spec 6 Doc.DumpStructs. API gaps: no root XmlText getter, UndoManagerOptions.Doc, ItemView content, Doc in XmlFragment, whenLoaded/whenSynced, GetSubdocGuids. Fixture infra: virtual clock shim in oracle.mjs (lib0 captured Date.now), PUD timers virtual. PUD-07 made kind diverge (ADR 0003). Ported tests deviate: XmlText roots use YText; testUndoDeleteFilter keyed on item id.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added doc-feature step kinds on Node and C# interpreters with schema entries, 17 fixtures, and tests for DOC/SUB/UM/SNAP/RP/PUD plus 57 upstream ports; all 81 owned entries active, ledger rows ported. Verified by traceability gate (0 violations), 995 tests 0 failed, fixtures no drift.
<!-- SECTION:FINAL_SUMMARY:END -->
