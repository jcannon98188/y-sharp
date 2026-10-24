---
id: TASK-4.41
title: '41. Differential II: sync and awareness'
status: To Do
assignee: []
created_date: '2026-10-24 19:42'
updated_date: '2026-10-24 19:42'
labels:
  - spec
  - 'model:opus'
milestone: m-7
dependencies:
  - TASK-4.37
  - TASK-4.40
parent_task_id: TASK-4
ordinal: 41000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Protocol coverage for the live differential.

**Owns:** `tests/YSharp.Interop.Tests` protocol generators
**Registry:** `DIFF` entries
**Handoff section-7 items:** Build the `sync.*` and awareness generators (`SharpSide.Extensions` hook).
**Suggested model:** opus (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The traceability gate passes and no pending registry entry is added
- [ ] #2 Protocol generators run in smoke and nightly; every mismatch fixed or raised as an owner decision
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 `dotnet build YSharp.slnx` has 0 warnings
- [ ] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [ ] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [ ] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->
