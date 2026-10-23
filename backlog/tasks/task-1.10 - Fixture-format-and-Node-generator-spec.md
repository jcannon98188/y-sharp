---
id: TASK-1.10
title: Fixture format and Node generator spec
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 19:55'
updated_date: '2026-10-23 21:04'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.2
  - TASK-1.7
  - TASK-1.8
parent_task_id: TASK-1
ordinal: 11000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

Decide the golden-fixture schema (ops script, update bytes V1/V2, expected doc state, versions, seed), file layout, determinism (explicit clientIDs), the Node generator's structure, and the drift check.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixtures are declarative versioned ops scripts replayed by Node and C# interpreters; tagged JSON values; per-scenario files with manifest and headers; byte-exact regenerate-and-diff drift job on pinned Node; recorded op logs for simulations; named observations; stored malformed cases with test-time truncation sweeps; thin generator in tools/fixture-gen. Recorded in docs/adr/0011-fixture-format-and-generator.md; glossary gained Ops script and Observation. Decided by grilling with the user, no code verification applicable.
<!-- SECTION:FINAL_SUMMARY:END -->
