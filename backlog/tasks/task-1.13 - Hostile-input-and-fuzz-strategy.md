---
id: TASK-1.13
title: Hostile-input and fuzz strategy
status: Done
assignee:
  - '@jason'
created_date: '2026-10-23 19:55'
updated_date: '2026-10-23 21:16'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.12
parent_task_id: TASK-1
ordinal: 14000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

Decide the hostile-input classes (truncated, bit-flipped, oversized lengths, deep nesting, invalid clocks, cycles), corpus sources, FsCheck generators, time/memory guards, and which tests run per-commit vs nightly.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided: nine hostile classes (HOS-*), corpus (fixed + generated seeds + mutations, non-gating SharpFuzz), three FsCheck layers (byte mutator, structure-aware builder, ops-script), guards (timeout, k·N allocation, small-stack probe, canary process, heap limit, consistency post-condition), per-commit/smoke/nightly tiers, oracle outcome-class comparison, tiny-limits passes, single-file regression rule. Recorded in docs/adr/0013-hostile-input-and-fuzz-strategy.md; GLOSSARY.md extended.
<!-- SECTION:FINAL_SUMMARY:END -->
