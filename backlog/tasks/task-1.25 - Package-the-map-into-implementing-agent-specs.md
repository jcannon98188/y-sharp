---
id: TASK-1.25
title: Package the map into implementing-agent specs
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 21:31'
updated_date: '2026-10-23 21:57'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.20
  - TASK-1.23
  - TASK-1.24
references:
  - docs/adr/0010-traceability-registry.md
  - docs/adr/0015-test-project-layout-tooling-and-ci.md
parent_task_id: TASK-1
ordinal: 26000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The map ends in work implementing agents can pick up without this conversation. That needs a breakdown by test project and area, an order, and a definition of done per spec, including populating docs/inventory/ and the 209-test ledger.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Work breakdown by project and area with ordering is decided
- [x] #2 Each spec has a definition of done tied to the traceability check (ADR 0010)
- [x] #3 Inventory population and JS-test ledger are assigned to specs
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Grilled with the user: 14 specs in four waves against the API stub, registry entries start pending and ratchet, done = owned IDs covered with checker green and no new pending. Registry population and the 209-test ledger assigned to spec 3, ownership by upstream file with randomized tests to spec 10. Recorded in docs/adr/0019-implementing-agent-spec-breakdown.md (planning only; reviewed in chat).
<!-- SECTION:FINAL_SUMMARY:END -->
