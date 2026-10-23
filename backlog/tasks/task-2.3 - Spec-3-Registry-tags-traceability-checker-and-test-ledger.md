---
id: TASK-2.3
title: 'Spec 3: Registry, tags, traceability checker and test ledger'
status: To Do
assignee: []
created_date: '2026-10-23 22:01'
labels: []
dependencies:
  - TASK-2.1
references:
  - docs/adr/0010-traceability-registry.md
  - docs/adr/0019-implementing-agent-spec-breakdown.md
  - docs/research
parent_task_id: TASK-2
ordinal: 30000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 0. Creates docs/inventory/ and the JS-test ledger so every later spec owns concrete IDs and rows. Must reconcile the upstream test count (research per-file counts sum to 210, ADR 0010 says 209).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 One YAML per area under docs/inventory/; every ID from the research notes and ADRs registered pending with source, kind, layers and owner spec; STR-nn, ENC-nn, ERR-nn assigned and frozen
- [ ] #2 Covers, PortOf and Diverges attributes exist; Covers is filterable as an xUnit v3 trait
- [ ] #3 Gating checker test enforces every ADR 0010 failure condition and writes the traceability matrix artifact
- [ ] #4 Ledger has one row per catalogued upstream test with an owner per ADR 0019 (randomized tests to spec 10); total reconciled and the discrepancy documented
- [ ] #5 Pending count high-water mark committed
<!-- AC:END -->
