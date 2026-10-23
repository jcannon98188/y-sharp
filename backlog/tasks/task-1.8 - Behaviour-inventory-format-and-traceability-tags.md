---
id: TASK-1.8
title: Behaviour inventory format and traceability tags
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 19:55'
updated_date: '2026-10-23 21:02'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.5
parent_task_id: TASK-1
ordinal: 9000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

How is each Yjs behaviour identified (ID scheme), recorded, and traced to xUnit tests, fixtures and ported JS tests? Decide the inventory file format, tag/attribute scheme on tests, and the automated check that every inventory entry has a test.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided the traceability scheme: existing IDs stay canonical (STR-/ENC-/ERR- added), YAML registry under docs/inventory/ as source of truth, [Covers]/[PortOf]/[Diverges] tags plus fixture-header covers, a reflection-based gating check with a traceability matrix artifact, layer rules (wire entries need fixtures), fixed exclusion reasons, a 209-test JS ledger, one registry for oracle behaviours, CC/ERR guarantees and BUD budgets, and a ratcheted pending status. Recorded in docs/adr/0010-traceability-registry.md. Decision only.
<!-- SECTION:FINAL_SUMMARY:END -->
