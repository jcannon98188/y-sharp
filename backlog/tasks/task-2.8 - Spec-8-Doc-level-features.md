---
id: TASK-2.8
title: 'Spec 8: Doc-level features'
status: In Progress
assignee:
  - '@claude'
created_date: '2026-10-23 22:01'
updated_date: '2026-10-24 01:27'
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
- [ ] #1 Tests cover DOC-*, UM-*, SNAP-*, RP-*, SUB-* and PUD-*
- [ ] #2 Upstream doc, undo-redo, snapshot and relativePositions tests have ledger outcomes
- [ ] #3 Traceability check passes; every owned registry entry is active or pending with a written reason and owner, and no new pending entries were added; owned ledger rows have an outcome other than pending; fixtures regenerate with no drift; gating tier within its time budget; traceability matrix attached to the task
- [ ] #4 Registers, on both the Node generator and the C# interpreter, the doc-feature step kinds of ADR 0016: gc.run, subdoc.add/load/destroy, snapshot.take/restore, relpos.create/resolve, undo.create/stopCapturing/undo/redo, pud.setUserDescription/observe; each with schema change and [Covers]
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Node: doc-feature step kinds (lib/steps-doc.mjs), ops.schema entries, scenarios/docfeatures.mjs -> fixtures/{doc,subdocs,snapshot,relpos,undo,pud}; npm run check no drift.
2. C#: DocFeatureSteps in TestKit (registered in StandardSteps), fixture replay theory in tests/YSharp.Tests/DocFeatures.
3. Unit and port tests with [Covers]/[PortOf] for DOC-*, SUB-*, UM-*, SNAP-*, RP-*, PUD-* and the 58 upstream ledger rows (doc, undo-redo, snapshot, relativePositions); oracle-derived goldens; seams via reflection probes (DocSeams).
4. Activate registry entries, set ledger outcomes, ratchet pending-high-water, attach matrix docs/inventory/matrices/spec-8-traceability.md.
5. Build/test/check, merge integration/verification-suite, finalize.
<!-- SECTION:PLAN:END -->
