---
id: TASK-2.5
title: 'Spec 5: Values and codecs'
status: In Progress
assignee:
  - '@claude'
created_date: '2026-10-23 22:01'
updated_date: '2026-10-24 00:50'
labels: []
dependencies:
  - TASK-2.2
  - TASK-2.3
  - TASK-2.4
references:
  - docs/adr/0007-yvalue-contract.md
  - docs/adr/0002-yvalue-pinned-any-mapping.md
  - docs/research/encoding.md
  - docs/research/json.md
  - docs/research/quirks.md
parent_task_id: TASK-2
ordinal: 32000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 2. YValue, the JS-compatible JSON writer and reader, V1/V2 encode and decode, merge, diff and convert.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Tests and fixtures cover all ENC-* entries and the number and string quirks (Q*)
- [ ] #2 Ported encoding, compatibility and updates upstream tests have ledger outcomes
- [ ] #3 Traceability check passes; every owned registry entry is active or pending with a written reason and owner, and no new pending entries were added; owned ledger rows have an outcome other than pending; fixtures regenerate with no drift; gating tier within its time budget; traceability matrix attached to the task
<!-- AC:END -->
