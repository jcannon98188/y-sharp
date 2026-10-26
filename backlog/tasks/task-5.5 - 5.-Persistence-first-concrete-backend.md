---
id: TASK-5.5
title: '5. Persistence: first concrete backend'
status: To Do
assignee: []
created_date: '2026-10-25 17:39'
labels:
  - server
  - hitl
  - 'model:sonnet'
dependencies:
  - TASK-5.4
parent_task_id: TASK-5
ordinal: 58000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The storage interface needs one real backend to be usable. The choice (file system, SQLite or another store) is the owner's and is recorded in the TASK-5.1 ADR. **Suggested model:** sonnet (override freely).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The chosen backend implements the storage interface and passes the same crash-recovery tests as the in-memory one
- [ ] #2 Concurrent documents do not block each other on the backend
- [ ] #3 Behaviour on partial writes and corrupted tail records is defined and tested
<!-- AC:END -->
