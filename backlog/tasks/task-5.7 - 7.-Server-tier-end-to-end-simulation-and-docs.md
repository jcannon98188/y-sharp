---
id: TASK-5.7
title: 7. Server-tier end-to-end simulation and docs
status: To Do
assignee: []
created_date: '2026-10-25 17:39'
labels:
  - server
  - 'model:sonnet'
dependencies:
  - TASK-5.5
  - TASK-5.6
  - TASK-5.3
parent_task_id: TASK-5
ordinal: 60000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Ties the relay tier, persistence and mailbox together and shows that real peers converge through them. Reuses the existing simulation harness. **Suggested model:** sonnet (override freely).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A simulation of N Yjs-protocol peers through the server tier with restarts in the middle converges to the same state as the Doc-only path
- [ ] #2 The existing YSharp.Tests, Fuzz and Interop suites pass unchanged
- [ ] #3 Docs describe the layering, the storage interface and how to host the server tier
<!-- AC:END -->
