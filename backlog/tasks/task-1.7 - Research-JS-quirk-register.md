---
id: TASK-1.7
title: 'Research: JS quirk register'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 19:55'
updated_date: '2026-10-23 20:01'
labels:
  - 'wayfinder:research'
dependencies: []
parent_task_id: TASK-1
ordinal: 8000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

List JS-specific quirks that affect a C# port and wire interop: number encoding (int/float32/float64/BigInt/-0/NaN/undefined), UTF-16 indexing in YText, key ordering, observer ordering, lib0 varint limits, Math.random clientIDs. Output: docs/research/quirks.md.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Wrote JS quirk register (32 entries: number/value encoding, varint/RLE limits incl -0, UTF-16/surrogate/UTF-8, client IDs (crypto uint32, not Math.random), ordering/observers/stable sort, awareness JSON, aliasing, time), each with source refs, repro idea, and match/diverge proposal; key lib0 claims verified under Node. File: docs/research/quirks.md
<!-- SECTION:FINAL_SUMMARY:END -->
