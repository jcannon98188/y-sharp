---
id: TASK-3.1
title: Faulted document behaviour (ERR-20)
status: Done
assignee:
  - '@claude'
created_date: '2026-10-24 16:52'
updated_date: '2026-10-24 16:52'
labels:
  - 'wayfinder:grilling'
dependencies: []
references:
  - docs/adr/0005-error-model-and-crash-safety.md
parent_task_id: TASK-3
ordinal: 44000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Question

The ADR 0005 faulted-document behaviour was chosen by a subagent during the suite build. Does the owner confirm it, and is the fault permanent?
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Owner confirms or changes the ERR-20 behaviour
- [x] #2 ADR 0005 states whether a fault is permanent and how a caller recovers
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Put the ERR-20 behaviour to the owner. 2. Record the answer in ADR 0005.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Owner confirmed the subagent's ERR-20 behaviour in chat on 2026-10-24 and agreed that the fault is permanent.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Confirmed: IsFaulted true; reads and EncodeStateAsUpdate work; mutators and ApplyUpdate throw YInternalException; TryApplyUpdate returns Rejected and changes nothing. The fault is permanent with no clear API; recover by loading EncodeStateAsUpdate into a fresh Doc. ADR 0005 updated. Verified by owner review in chat.
<!-- SECTION:FINAL_SUMMARY:END -->
