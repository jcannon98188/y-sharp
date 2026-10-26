---
id: TASK-5.5
title: '5. Persistence: default in-memory backend and conformance suite'
status: To Do
assignee: []
created_date: '2026-10-25 17:39'
updated_date: '2026-10-26 20:45'
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
ADR 0024 SRV-12 (owner decision, 2026-10-26): YSharp.Server ships an in-memory store as the default backend and the storage interface for user-provided backends, but no file or database backend. The in-memory store from TASK-5.4 becomes a supported default, and a reusable conformance suite lets any user backend prove it meets the storage contract. **Suggested model:** sonnet (override freely).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The in-memory store is the server's default backend when the host registers none, and a user-provided IUpdateStore replaces it through one registration point
- [ ] #2 Concurrent documents do not block each other on the in-memory store, and its operations are safe to call concurrently
- [ ] #3 A public, reusable conformance suite covers crash at each step of append and compaction, partial writes, corrupted tail records, idempotent replay and concurrent documents, and the in-memory store passes it
- [ ] #4 The contract for partial writes and corrupted tail records is defined in the interface documentation
- [ ] #5 The documentation states that the in-memory default is not durable and loses all documents on restart
<!-- AC:END -->
