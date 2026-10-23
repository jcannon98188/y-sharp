---
id: TASK-2.13
title: 'Spec 13: Live differential harness'
status: To Do
assignee: []
created_date: '2026-10-23 22:01'
labels: []
dependencies:
  - TASK-2.4
  - TASK-2.5
  - TASK-2.6
  - TASK-2.7
references:
  - docs/adr/0012-live-differential-harness.md
  - docs/adr/0013-hostile-input-and-fuzz-strategy.md
parent_task_id: TASK-2
ordinal: 40000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 3. YSharp.Interop.Tests against a live Node child.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Stateless ops scripts over NDJSON with FsCheck generator, virtual clock, timeouts and oracle-misbehaved handling
- [ ] #2 Smoke PR job and nightly slow tier run; failures replayable and saved as fixture candidates
- [ ] #3 Protocol message catalogue, per-divergence normalisers and delivery-schedule steps specified and implemented
<!-- AC:END -->
