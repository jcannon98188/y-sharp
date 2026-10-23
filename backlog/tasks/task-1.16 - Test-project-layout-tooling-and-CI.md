---
id: TASK-1.16
title: 'Test project layout, tooling and CI'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 19:55'
updated_date: '2026-10-23 21:30'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.8
  - TASK-1.10
parent_task_id: TASK-1
ordinal: 17000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

Decide solution/project layout, package list (xUnit v3, FsCheck, others), shared test helpers (JS test helper port, PRNG), naming conventions, Node/.NET CI, fixture-drift gate.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided the test project layout and tooling: YSharp.slnx with src/tests/bench/tools/fixtures split, shared YSharp.TestKit (helper port, ops interpreter, fixture loader, registry reader, allocation helper, Node client), bit-exact lib0 PRNG port, xUnit v3 + FsCheck + YamlDotNet with plain Assert, central package management, Tier traits with serial allocation and stress collections, Linux-first GitHub Actions (gating on three OSes, drift, smoke, nightly), report-only coverage. Recorded in docs/adr/0015-test-project-layout-tooling-and-ci.md. Decision only; nothing to execute.
<!-- SECTION:FINAL_SUMMARY:END -->
