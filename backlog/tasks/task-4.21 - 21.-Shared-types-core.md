---
id: TASK-4.21
title: 21. Shared types core
status: To Do
assignee: []
created_date: '2026-10-24 19:42'
updated_date: '2026-10-24 19:42'
labels:
  - spec
  - 'model:sonnet'
milestone: m-4
dependencies:
  - TASK-4.17
parent_task_id: TASK-4
ordinal: 21000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The common machinery under every shared type: `YType`, list and map cores, search markers, `Doc.Get*` and `YUntyped`, `YEntry` content and `ToJson`.

**Features:** `TypesCore`
**Owns:** `src/YSharp/Types/` (core), `Doc.Types.cs`
**Registry:** `TX` and `Y` core entries, `Q` type quirks (Q10, Q29)
**Test files:** `Types/SharedTypeTests.cs`
**Suggested model:** sonnet (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [ ] #2 This spec's features are added to `Features.Landed`
- [ ] #3 The traceability gate passes and no pending registry entry is added
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 `dotnet build YSharp.slnx` has 0 warnings
- [ ] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [ ] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [ ] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->
