---
id: TASK-4.43
title: 43. Remaining CI jobs on a real runner
status: To Do
assignee: []
created_date: '2026-10-24 19:42'
updated_date: '2026-10-24 19:42'
labels:
  - spec
  - 'model:sonnet'
  - hitl
milestone: m-7
dependencies:
  - TASK-4.7
  - TASK-4.42
parent_task_id: TASK-4
ordinal: 43000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Three CI paths have never run anywhere. Running them needs the pushed repository and secrets or runners only the owner controls.

**Owns:** `.github/workflows/` fixes only
**Handoff section-7 items:** Coverage summary step, AFL++/SharpFuzz job, nightly benchmark comparison: never run.
**Suggested model:** sonnet (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Coverage summary, SharpFuzz/AFL++ and nightly benchmark comparison jobs run green on a real runner, fixes committed
- [ ] #2 The traceability gate passes and no pending registry entry is added
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 `dotnet build YSharp.slnx` has 0 warnings
- [ ] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [ ] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [ ] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->
