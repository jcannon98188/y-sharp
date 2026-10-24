---
id: TASK-4.5
title: '5. Spike candidate B: chunked arenas'
status: To Do
assignee: []
created_date: '2026-10-24 19:41'
updated_date: '2026-10-24 19:42'
labels:
  - spec
  - 'model:opus'
milestone: m-0
dependencies:
  - TASK-4.3
parent_task_id: TASK-4
ordinal: 5000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Second storage model for the M0 spike: structs in chunked arenas addressed by index.

**Owns:** `spike/YSharp.StoreSpike/Arenas/`
**Suggested model:** opus (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Implements the same ADR 0018 spike operation list as candidate A on the shared content layer
- [ ] #2 Has a stated index-reuse policy (generation check or no reuse) and a deleted-payload release rule
- [ ] #3 Runs every workload through the driver and shows how it fits behind an `ItemRef` handle
- [ ] #4 The traceability gate passes and no pending registry entry is added
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 `dotnet build YSharp.slnx` has 0 warnings
- [ ] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [ ] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [ ] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->
