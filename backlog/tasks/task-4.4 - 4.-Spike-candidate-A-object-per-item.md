---
id: TASK-4.4
title: '4. Spike candidate A: object per item'
status: Done
assignee: []
created_date: '2026-10-24 19:41'
updated_date: '2026-10-24 21:20'
labels:
  - spec
  - 'model:opus'
milestone: m-0
dependencies:
  - TASK-4.3
parent_task_id: TASK-4
ordinal: 4000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
First of the two storage models the M0 spike compares (ADR 0018 spike amendment).

**Owns:** `spike/YSharp.StoreSpike/ObjectPerItem/`
**Suggested model:** opus (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Implements the ADR 0018 spike operation list (client table and split, links, local insert and delete, Yjs merge rules, search markers or equivalent, delete set, map key table, deleted-payload release, chunks of 64 KB or less) on the shared content layer
- [x] #2 Runs every workload through the driver; its structure leaves room for remote integration
- [x] #3 Shows how it fits behind an `ItemRef` handle (ADR 0021)
- [x] #4 The traceability gate passes and no pending registry entry is added
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 `dotnet build YSharp.slnx` has 0 warnings
- [x] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [x] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [x] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented on the impl/task-4 integration branch and merged; verified by the implementer with a 0-warning build, green Debug and Release test runs, clean fixture drift check and the traceability gate (see PR #1 and the task comments).
<!-- SECTION:FINAL_SUMMARY:END -->
