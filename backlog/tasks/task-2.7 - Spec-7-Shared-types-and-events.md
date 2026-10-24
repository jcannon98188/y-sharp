---
id: TASK-2.7
title: 'Spec 7: Shared types and events'
status: To Do
assignee: []
created_date: '2026-10-23 22:01'
updated_date: '2026-10-24 00:51'
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
- [ ] #1 Tests and fixtures cover all TX-* and the other types-note prefixes
- [ ] #2 Deterministic upstream y-map, y-array, y-text and y-xml tests have ledger outcomes
- [ ] #3 Traceability check passes; every owned registry entry is active or pending with a written reason and owner, and no new pending entries were added; owned ledger rows have an outcome other than pending; fixtures regenerate with no drift; gating tier within its time budget; traceability matrix attached to the task
- [ ] #4 Registers, on both the Node generator and the C# interpreter, the step kinds ADR 0016 assigns to this area: text.insertEmbed, text.applyDelta, array.insertType, map.insertType (Node side exists from Spec 4, C# side missing), and xml.insert/delete/setAttribute/removeAttribute with text.* on XmlText (neither side); each with schema change and [Covers]
<!-- AC:END -->
