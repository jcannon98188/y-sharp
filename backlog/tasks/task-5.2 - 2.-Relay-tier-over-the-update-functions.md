---
id: TASK-5.2
title: 2. Relay tier over the update functions
status: To Do
assignee: []
created_date: '2026-10-25 17:38'
labels:
  - server
  - 'model:sonnet'
dependencies:
  - TASK-5.1
parent_task_id: TASK-5
ordinal: 55000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Most server work is receive, persist and fan out, and needs no Doc. The core already has YUpdate.TryMerge, TryDiff and TryStateVectorFromUpdate (spec 16) with YLimits checks, but they are stateless functions. This task adds a small stateful relay type that keeps a document's merged update and state vector and answers sync requests without an Item graph. **Suggested model:** sonnet (override freely).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A relay type accepts update bytes, validates them against YLimits, folds them into its merged update and state vector, and returns a diff for a client state vector, without constructing a Doc
- [ ] #2 Its merged output and diffs are byte-identical to the Doc-based path (EncodeStateAsUpdate, ApplyUpdate) on the existing fixtures and under differential fuzzing, for V1 and V2
- [ ] #3 Invalid or over-limit updates return a YStatus error and leave the relay state unchanged
- [ ] #4 Tests cover empty relay, out-of-order and duplicate updates, gaps that become Skips, and V1 and V2 inputs
<!-- AC:END -->
