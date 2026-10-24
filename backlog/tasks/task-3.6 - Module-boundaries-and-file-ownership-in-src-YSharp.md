---
id: TASK-3.6
title: Module boundaries and file ownership in src/YSharp
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 16:52'
updated_date: '2026-10-24 17:30'
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
- [x] #1 Module list with responsibilities and owning milestone is decided
- [x] #2 File ownership rules for parallel specs and the change-request path to core modules are decided
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Read the stub and ADR 0015. 2. Grill the module cut, seams and ownership using codebase-design vocabulary. 3. Record as an ADR.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Owner agreed Q1-Q7 in chat on 2026-10-24.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
One assembly split into module folders (Encoding and Values in M1; Store, Integration, Transactions and Codecs in M2; V2 in M3; Types and Events in M4; DocFeatures sub-folders in M5; Protocols sub-folders in M6). Storage is hidden behind an opaque ItemRef handle with no IStore. The codec seam is generic over struct encoder/decoder adapters. Later milestones hook transaction cleanup through partial methods declared by M2. Public types that span owners are partial classes with one file per owner. Additive internal changes to another module are allowed and noted; any other change goes through a serialized task against the owner. An M0 'Source layout skeleton' spec splits the stub with no behaviour change. Recorded in ADR 0021. Verified by owner review in chat.
<!-- SECTION:FINAL_SUMMARY:END -->
