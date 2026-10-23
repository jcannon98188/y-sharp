---
id: TASK-2.3
title: 'Spec 3: Registry, tags, traceability checker and test ledger'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 22:01'
updated_date: '2026-10-23 22:45'
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
- [x] #1 One YAML per area under docs/inventory/; every ID from the research notes and ADRs registered pending with source, kind, layers and owner spec; STR-nn, ENC-nn, ERR-nn assigned and frozen
- [x] #2 Covers, PortOf and Diverges attributes exist; Covers is filterable as an xUnit v3 trait
- [x] #3 Gating checker test enforces every ADR 0010 failure condition and writes the traceability matrix artifact
- [x] #4 Ledger has one row per catalogued upstream test with an owner per ADR 0019 (randomized tests to spec 10); total reconciled and the discrepancy documented
- [x] #5 Pending count high-water mark committed
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Generate docs/inventory area YAMLs (one per prefix/area) from research notes and ADRs; assign STR/ENC/ERR (+BUD/HOS/SIM) frozen; ledger of 210 rows (209 yjs + 1 y-protocols; ADR 0010's 209 counts yjs only)
2. TestKit/Traceability: Covers/PortOf/Diverges ITraitAttribute attributes, registry model + YamlDotNet loader, checker, matrix writer (TDD against synthetic inputs)
3. Gating test in YSharp.Tests: runs checker on real inventory, test assemblies, fixtures, upstream sources; writes matrix to TestResults/traceability; high-water mark file
4. Switch SIM placeholder Trait(Covers) tests to [Covers]; upload matrix artifact in gating.yml
5. Verify AC with build and tests, final summary
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Registry: 576 entries in 24 area YAMLs under docs/inventory (one file per ID prefix so specs do not share files; quirks split by research-note section). Generated once from the research notes and ADRs by a throwaway script; STR-01..73 follow structs.md labels A1..L5, ENC-01..85 follow encoding.md sections/table rows, ERR-01..21 follow ADR 0005 bullets, BUD-01..14 (11 hot paths + 3 other ADR 0009 budgets), HOS-* from ADR 0013, SIM-01..05 (spec 10, diverge) and SIM-10..14 (spec 2, active, covered by SimHarnessTests). id-lock.txt freezes all IDs; the gate fails if one disappears. Fields best-effort (layers, wire, kind heuristics); owning specs refine.

Ledger: 210 rows. Reconcile: yjs/tests has 209 test exports (3+11+4+9+12+26+6+42+41+44+11) plus 1 in y-protocols/awareness.test.js = 210; ADR 0010's 209 is the yjs suite alone. 49 randomized rows (48 testRepeat* + testInsertAndDeleteAtRandomPositions) owned by spec 10; the gate also compares ledger to the upstream sources so a gap either way fails.

Checker: pure TraceabilityChecker in TestKit/Traceability (rules TR-*), InventoryLoader (YamlDotNet), TestScanner (reflection incl. sibling Fuzz/Interop assemblies, layer inferred from assembly, PortOf=port), MatrixWriter -> TestResults/traceability/. Decisions: gated layers are unit (unit or port test), port, fixture; differential/fuzz only reported; pending high-water mark fails only when exceeded (no forced ratchet, avoids merge conflicts); YSHARP_RELEASE_GATE=1 requires zero pending. CI uploads the matrix.

Validation: dotnet build clean; dotnet test --solution passes Debug (173 total, 0 failed) and Release; --filter-trait Covers=SIM-10 selects 2 tests.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Created docs/inventory (24 area YAMLs, 576 pending/active entries incl. frozen STR/ENC/ERR, id-lock.txt), ledger.yaml (210 rows, 209+1 reconciled, randomized rows to spec 10), Covers/PortOf/Diverges attributes (xUnit v3 ITraitAttribute), a pure traceability checker with one unit test per ADR 0010 failure condition, a gating test that runs it on the real repo and writes the matrix artifact, and pending-high-water.json (571 registry, 210 ledger). SIM-10..14 now use [Covers]. Verified by dotnet build and dotnet test --solution (Debug and Release, 0 failures) and a trait filter run.
<!-- SECTION:FINAL_SUMMARY:END -->
