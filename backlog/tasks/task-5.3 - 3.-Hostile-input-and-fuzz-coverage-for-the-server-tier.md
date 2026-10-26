---
id: TASK-5.3
title: 3. Hostile-input and fuzz coverage for the server tier
status: To Do
assignee: []
created_date: '2026-10-25 17:38'
labels:
  - server
  - 'model:opus'
dependencies:
  - TASK-5.2
parent_task_id: TASK-5
ordinal: 56000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The relay tier and mailbox accept bytes from untrusted peers and hold state across many updates, so growth of pending state and repeated bad input matter more than for a single apply. Extends ADR 0013 to these paths. **Suggested model:** opus (override freely): triage of any core bug found here goes through the ADR 0022 evidence rules.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Fuzz targets cover the relay type and the mailbox entry point, with a post-condition that nothing stays rented or retained after a rejected update
- [ ] #2 Relay memory stays bounded by YLimits under a stream of adversarial updates (huge clocks, missing dependencies, many client ids, decompression-style size amplification)
- [ ] #3 Any core bug found is recorded with oracle evidence per ADR 0022, and fixed or escalated as an owner decision
<!-- AC:END -->
