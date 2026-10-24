---
id: TASK-4.40
title: '40. Differential I: XML, nested types and subdocs'
status: To Do
assignee: []
created_date: '2026-10-24 19:42'
updated_date: '2026-10-24 19:42'
labels:
  - spec
  - 'model:opus'
milestone: m-7
dependencies:
  - TASK-4.28
  - TASK-4.34
parent_task_id: TASK-4
ordinal: 40000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The live differential only generates text, array and map edits and compares `updates`, `state`, `sv`, `doc` and `results`.

**Owns:** `tests/YSharp.Interop.Tests` generators and comparators
**Registry:** `DIFF` entries
**Test files:** `YSharp.Interop.Tests/Tests/*`
**Handoff section-7 items:** Build the XML, nested-type and subdoc generators; compare `events`, `structs` and `ds` too.
**Suggested model:** opus (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The traceability gate passes and no pending registry entry is added
- [ ] #2 New generators and compared fields run in smoke and nightly; every mismatch fixed or raised as an owner decision (ADR 0022)
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 `dotnet build YSharp.slnx` has 0 warnings
- [ ] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [ ] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [ ] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->
