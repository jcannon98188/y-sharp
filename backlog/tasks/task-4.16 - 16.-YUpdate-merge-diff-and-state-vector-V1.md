---
id: TASK-4.16
title: '16. YUpdate merge, diff and state vector (V1)'
status: To Do
assignee: []
created_date: '2026-10-24 19:41'
updated_date: '2026-10-24 19:42'
labels:
  - spec
  - 'model:sonnet'
milestone: m-2
dependencies:
  - TASK-4.14
parent_task_id: TASK-4
ordinal: 16000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Doc-free update functions on V1 bytes, with optional `YLimits` (api-decisions).

**Features:** `UpdateFunctions`
**Owns:** `src/YSharp/Codecs/UpdateFunctions/` (V1)
**Registry:** V1 update-function `ENC` entries
**Test files:** `Codecs/UpdateFunctionTests.cs` (V1), `YSharp.Fuzz.Tests/Tests/AmplificationTests.cs` (V1)
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
