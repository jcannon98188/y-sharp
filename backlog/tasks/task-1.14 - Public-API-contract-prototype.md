---
id: TASK-1.14
title: Public API contract prototype
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 19:55'
updated_date: '2026-10-23 20:49'
labels:
  - 'wayfinder:prototype'
dependencies:
  - TASK-1.3
  - TASK-1.4
  - TASK-1.9
  - TASK-1.12
  - TASK-1.18
parent_task_id: TASK-1
ordinal: 15000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

Prototype the C# public API surface (Doc, shared types, YValue, updates, transactions, events, UndoManager, awareness) the tests will compile against. Use Span/IBufferWriter-style, low-allocation shapes. Output: a reviewed sketch the implementing agents turn into a compiling stub.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Reviewed and approved the C# API sketch in docs/prototype/api-sketch/ (builds with dotnet build, 0 errors; all bodies throw). Decisions recorded in docs/adr/0008-public-api-shape.md: callback+ref-struct transactions with implicit joining, pooled dispatch-scoped events, YEntry list/map content, empty reads on unintegrated types, three recorded divergences (array indexer throws, invariant XML lowercasing, no placeholder on root-name position resolve). Prototype is throwaway; implementing agents turn it into the compiling stub.
<!-- SECTION:FINAL_SUMMARY:END -->
