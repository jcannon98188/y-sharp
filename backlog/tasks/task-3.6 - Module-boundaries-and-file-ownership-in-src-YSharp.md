---
id: TASK-3.6
title: Module boundaries and file ownership in src/YSharp
status: To Do
assignee: []
created_date: '2026-10-24 16:52'
updated_date: '2026-10-24 16:52'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-3.4
references:
  - docs/adr/0015-test-project-layout-tooling-and-ci.md
  - docs/adr/0006-concurrency-contract.md
parent_task_id: TASK-3
ordinal: 49000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

How is `src/YSharp` cut into internal modules (folders, namespaces, the deep-module seams between store, integrate, codecs, transaction and lock, types, events, doc features, protocols) so the sequential core and the parallel fan-out can be specced with no two specs editing the same files? Who owns shared internals, and how does a parallel spec request a change to the core?
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Module list with responsibilities and owning milestone is decided
- [ ] #2 File ownership rules for parallel specs and the change-request path to core modules are decided
<!-- AC:END -->
