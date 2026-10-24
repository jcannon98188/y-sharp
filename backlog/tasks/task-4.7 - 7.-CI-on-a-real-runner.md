---
id: TASK-4.7
title: 7. CI on a real runner
status: To Do
assignee: []
created_date: '2026-10-24 19:41'
updated_date: '2026-10-24 19:42'
labels:
  - spec
  - 'model:sonnet'
  - hitl
milestone: m-0
dependencies:
  - TASK-4.1
parent_task_id: TASK-4
ordinal: 7000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The workflows have never run (nothing is pushed). Pushing and getting the core jobs green on a real runner early surfaces breakage while it is cheap. The push itself is the owner's.

**Owns:** `.github/workflows/` fixes only
**Handoff section-7 items:** CI workflows never run on a real runner (gating, drift, smoke).
**Suggested model:** sonnet (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Owner has pushed the repository to GitHub
- [ ] #2 Gating, fixture-drift and smoke jobs run green on a real runner, with any workflow fixes committed
- [ ] #3 The traceability gate passes and no pending registry entry is added
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 `dotnet build YSharp.slnx` has 0 warnings
- [ ] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [ ] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [ ] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->
