---
id: TASK-1.20
title: Ops-script vocabulary and canonical dump formats
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 21:31'
updated_date: '2026-10-23 21:33'
labels:
  - 'wayfinder:grilling'
dependencies: []
references:
  - docs/adr/0011-fixture-format-and-generator.md
  - docs/adr/0012-live-differential-harness.md
parent_task_id: TASK-1
ordinal: 21000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
ADR 0011 fixes the ops-script idea but not the step kinds. The differential harness (ADR 0012), fuzz op logs (ADR 0013) and every area spec reuse the vocabulary, so it blocks most remaining spec detail.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Step kinds and their fields are listed per area (core types, sync, awareness, snapshots, relative positions, UndoManager, subdocs, PermanentUserData)
- [ ] #2 Canonical dump formats for the doc and structs observations are defined
- [ ] #3 The per-offset truncation outcome list format is defined
- [ ] #4 Recorded as an ADR
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided the ops-script vocabulary and dump formats: named replicas with explicit client ids, root/path addressing, transact steps with origin, no interpreter-invented defaults, step kinds per area with a normative JSON schema, typed doc tree, clock-ordered structs dump, run-length truncation outcome list. Recorded in docs/adr/0016-ops-script-vocabulary-and-dump-formats.md. Decision only; nothing to execute.
<!-- SECTION:FINAL_SUMMARY:END -->
