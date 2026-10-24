---
id: TASK-2.7
title: 'Spec 7: Shared types and events'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 22:01'
updated_date: '2026-10-24 01:37'
labels: []
dependencies:
  - TASK-2.2
  - TASK-2.3
  - TASK-2.4
references:
  - docs/research/types.md
  - docs/adr/0008-public-api-shape.md
parent_task_id: TASK-2
ordinal: 34000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 2. Text, Array, Map and Xml behaviour plus observer and event ordering.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Tests and fixtures cover all TX-* and the other types-note prefixes
- [x] #2 Deterministic upstream y-map, y-array, y-text and y-xml tests have ledger outcomes
- [x] #3 Traceability check passes; every owned registry entry is active or pending with a written reason and owner, and no new pending entries were added; owned ledger rows have an outcome other than pending; fixtures regenerate with no drift; gating tier within its time budget; traceability matrix attached to the task
- [x] #4 Registers, on both the Node generator and the C# interpreter, the step kinds ADR 0016 assigns to this area: text.insertEmbed, text.applyDelta, array.insertType, map.insertType (Node side exists from Spec 4, C# side missing), and xml.insert/delete/setAttribute/removeAttribute with text.* on XmlText (neither side); each with schema change and [Covers]
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Node generator: add xml.insert/delete/setAttribute/removeAttribute (text.* already works on XmlText), schema entries, scenarios for text/array/map/xml/events (fixtures regenerate).
2. C# interpreter (tests/YSharp.Tests/Types/TypeSteps.cs): text.applyDelta + xml.* , reconcile with Spec 5 CodecSteps (insertEmbed/insertType registered only if absent); fixture replay with doc-dump, events, wire comparison.
3. Tests per area with [Covers]/[PortOf]: prelim/shared (T-0.*), array, map, text/format/embed/delta, xml, events (V,Y,A-E,M-E), transactions (TX-*), quirks Q23/24/26/27/29/10; ports of 89 upstream ledger rows; skip on stub per ADR 0019.
4. Activate registry entries, set ledger outcomes, ratchet pending mark, attach matrix.
5. Verify: dotnet build/test, npm run check; merge integration/verification-suite.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Evidence: dotnet build YSharp.slnx Debug and Release 0 warnings/0 errors. dotnet test --solution YSharp.slnx: 778 total, 0 failed, 169 passed, 609 skipped (skips are tests that need the implementation; each skips on NotImplementedException from the API stub, ADR 0019); gating tier about 3 s. Traceability gate passes with 0 violations: 141 owned entries (T-0.*, A-*, A-E*, M-*, M-E*, T-*, F-*, E-*, D-*, X-1/2, Y-*, X-E*, V-*, TX-*, Q10, Q23, Q24, Q26, Q27, Q29) all active, 0 pending, no new pending. Diverge entries A-7 (ADR 0008), M-E6 and TX-7 (ADR 0005) were match and are now diverge with [Diverges]; Q10 and Q24 already were. Ledger: 84 of 89 owned rows ported, 5 skipped with reasons (testIterators, testCustomTypings, testBestCase, testLargeFragmentedDocument, testIncrementalUpdatesPerformanceOnLargeFragmentedDocument). Pending mark ratcheted to registry 239 / ledger 108 after merging Spec 5 and Spec 6. Fixtures: 14 new scenarios in tools/fixture-gen/scenarios/types.mjs (area text/array/map/xml/events), npm run check reports 70 files up to date. Step kinds: Node gains xml.insert, xml.delete, xml.setAttribute, xml.removeAttribute (schema + steps.mjs; observe also records XmlEvent fields); C# TypeSteps registers text.applyDelta, xml.*, and an observe that records event payloads, on top of Spec 5's CodecSteps (insertEmbed, array.insertType, map.insertType are reused, not duplicated); StepKindTests carry [Covers] and assert registry/schema parity. Sketch edits (src/YSharp/3-Types.cs): TextDeltaOp constructor, YText.GetAttributes(). Matrix: docs/inventory/matrices/spec-7-traceability.md. Q25 (owner 6, pending) got a unit test (stable deep-event order) but stays pending: its fixture layer needs a deep-observer recorder step.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added the shared-types and events suite: 14 Node-generated fixtures (text, array, map, xml, events) replayed through a C# typed-dump and event comparison, xml.* and text.applyDelta step kinds on both interpreters (schema, Node, C#, [Covers] tests with registry/schema parity), 12 test files with unit, port and diverge tests, ledger outcomes for all 89 owned rows, and activation of all 141 owned registry entries. Verified with dotnet build/test (0 failed, stub tests skip), the traceability gate (0 violations), npm run check (fixtures up to date) and the Spec 7 matrix extract.
<!-- SECTION:FINAL_SUMMARY:END -->
