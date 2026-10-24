---
id: TASK-4.27
title: 27. Events and observers
status: To Do
assignee: []
created_date: '2026-10-24 19:42'
updated_date: '2026-10-24 19:42'
labels:
  - spec
  - 'model:opus'
milestone: m-4
dependencies:
  - TASK-4.21
parent_task_id: TASK-4
ordinal: 27000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Event building, pooled dispatch-scoped events, observe and observe-deep, doc transaction events and observer-fault containment (ADR 0005, ADR 0008), implemented through the cleanup hooks.

**Features:** `Events`
**Owns:** `src/YSharp/Events/`, `Doc.Events.cs`, the observer hook implementations
**Registry:** `E` entries, observer-fault `ERR` entries, Q23-Q25
**Test files:** `Types/EventTests.cs`, `Types/TransactionTests.cs`, `Structs/TransactionTests.cs`, `Concurrency/ObserverTests.cs`
**Suggested model:** opus (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [ ] #2 This spec's features are added to `Features.Landed`
- [ ] #3 The traceability gate passes and no pending registry entry is added
- [ ] #4 An `OutOfMemoryException` from an observer propagates and is not an observer fault (ERR-19)
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 `dotnet build YSharp.slnx` has 0 warnings
- [ ] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [ ] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [ ] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->
