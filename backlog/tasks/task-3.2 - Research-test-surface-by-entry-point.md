---
id: TASK-3.2
title: 'Research: test surface by entry point'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 16:52'
updated_date: '2026-10-24 17:00'
labels:
  - 'wayfinder:research'
dependencies: []
references:
  - docs/adr/0018-memory-layout-requirements.md
  - docs/inventory
parent_task_id: TASK-3
ordinal: 45000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

For every test file in `tests/YSharp.Tests`, `tests/YSharp.Fuzz.Tests` and `tests/YSharp.Interop.Tests` (and the slow memory tier in `bench/`), which public entry points and internal seams must work before its tests stop skipping? Group by registry ID prefix. The result is the dependency data for cutting milestones: e.g. how many STR tests drive integrate through `ApplyUpdate` (V1), through local type edits, or through a seam.

Output: `docs/research/test-surface.md` with a table per test file (file, test count, registry prefixes, required entry points, first plausible milestone under ADR 0018 as written) and a list of tests that cannot pass under the ADR 0018 order.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 `docs/research/test-surface.md` covers every test file with skipped tests
- [x] #2 Each file lists required public entry points and seams
- [x] #3 Tests that the ADR 0018 milestone order cannot turn green are listed with the reason
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Written to `docs/research/test-surface.md` (1,220 test methods across 102 files). Findings: (1) the stub skip is one global probe (`ApiStub.IsStub` checks only `YValue.Null`), so the first implemented member turns every unimplemented test into a failure; (2) nearly every multi-replica and STR test needs V1 encode and apply plus a transaction, so "store and integrate" cannot close alone; (3) mutators need transactions, but ADR 0018 puts types (M4) before transactions (M5); (4) doc features and the YValue/StateVector layer belong to no ADR 0018 milestone (~142 DocFeatures attributes); (5) BUD-08 spans V1 and V2; the M0 spike needs a private harness. Key claims spot-checked against ApiStub.cs and DocSupport.cs.
<!-- SECTION:FINAL_SUMMARY:END -->
