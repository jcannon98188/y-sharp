---
id: TASK-5.4
title: '4. Persistence: update log, compaction and recovery'
status: To Do
assignee: []
created_date: '2026-10-25 17:38'
labels:
  - server
  - 'model:opus'
dependencies:
  - TASK-5.2
parent_task_id: TASK-5
ordinal: 57000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A server must survive restarts without keeping every document in memory. The design is an append-only update log per document, periodic compaction by merging updates (no Doc load, stored as V2), and recovery on load. Crash consistency is the hard part: a crash between append and compaction must not lose or duplicate state. **Suggested model:** opus (override freely).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A storage interface from the ADR in TASK-5.1 plus an in-memory implementation used by tests
- [ ] #2 Updates are appended durably before they are acknowledged or fanned out, and an optional sequence number identifies each
- [ ] #3 Compaction merges the log via the relay tier into one V2 update and swaps it in atomically
- [ ] #4 Recovery after a simulated crash at every step of append and compaction yields a state equal to the pre-crash state, with no loss and no double apply
- [ ] #5 Replaying the same log twice is idempotent
<!-- AC:END -->
