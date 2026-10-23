---
id: TASK-1.9
title: YValue mapping for JS Any values
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 19:55'
updated_date: '2026-10-23 20:43'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.2
  - TASK-1.7
parent_task_id: TASK-1
ordinal: 10000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

Pin the C# YValue discriminated struct: undefined/null/bool/int/float32/float64/BigInt/string/bytes/array/object, rules for int vs float encoding selection, NaN/-0, nesting limits, and allocation-free reads.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Pinned YValue: nine cases (Number=double, BigInt=long, no wire tag), NaN canonical on encode, -0 preserved, insertion-ordered objects sorted JS-style only on encode, __proto__ diverges, YLimits-bounded iterative walks, one tree type for Any/V1 JSON/awareness, 16-byte internal layout, copy-on-decode, immutable, wire equality vs AttributeEquals, explicit BigInt, TryGet*/As* accessors. Recorded in docs/adr/0007-yvalue-contract.md. Decided in a two-round grilling; no code to verify.
<!-- SECTION:FINAL_SUMMARY:END -->
