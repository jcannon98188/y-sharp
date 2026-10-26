---
id: TASK-5.6
title: 6. Per-document mailbox and shared broadcast
status: To Do
assignee: []
created_date: '2026-10-25 17:39'
updated_date: '2026-10-25 18:06'
labels:
  - server
  - 'model:sonnet'
dependencies:
  - TASK-5.1
  - TASK-5.2
parent_task_id: TASK-5
ordinal: 59000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Work on one document must be serialized while different documents run in parallel, and one accepted update should be encoded once and shared across all connected peers rather than re-encoded per socket. Follows the layering in the TASK-5.1 ADR. **Suggested model:** sonnet (override freely).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A per-document mailbox serializes apply, persist and broadcast for one document, and documents do not block each other
- [ ] #2 An accepted update is encoded once and the same buffer is handed to every subscriber
- [ ] #3 A slow or disconnected subscriber cannot block the mailbox: backpressure policy is defined and tested
- [ ] #4 Tests show ordering per document, isolation between documents, and cancellation and shutdown without losing accepted updates
- [ ] #5 A registered validation hook (TASK-5.1 ADR) runs inside the mailbox before an update is persisted or broadcast; a rejected update is dropped, never persisted or broadcast, and the sender is resynced
- [ ] #6 Documents with no hook registered use the structural tier only and never materialize a Doc
- [ ] #7 Tests cover accept, reject, a hook that throws or times out (treated as reject), and a read-only connection whose updates are dropped without a Doc
<!-- AC:END -->
