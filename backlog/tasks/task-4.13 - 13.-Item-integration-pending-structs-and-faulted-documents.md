---
id: TASK-4.13
title: '13. Item integration, pending structs and faulted documents'
status: To Do
assignee: []
created_date: '2026-10-24 19:41'
updated_date: '2026-10-24 19:42'
labels:
  - spec
  - 'model:opus'
milestone: m-2
dependencies:
  - TASK-4.12
parent_task_id: TASK-4
ordinal: 13000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The Yjs conflict-resolution core (`Item.integrate`), pending-struct retries, rejected structs and the faulted-document state (ADR 0005, ERR-20).

**Features:** `Integrate`
**Owns:** `src/YSharp/Integration/` (except GC)
**Registry:** integration `STR` entries, `ERR-20` and integration `ERR` entries
**Test files:** `Structs/IntegrationTests.cs`, `Structs/ErrorPathTests.cs`, `Structs/UpdateApplicationTests.cs` (with spec 14), `YSharp.Fuzz.Tests/Tests/ErrorModelTests.cs` (fault cases)
**Seams provided:** `Doc.InjectInvariantViolation()`
**Suggested model:** opus (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [ ] #2 This spec's features are added to `Features.Landed`
- [ ] #3 The traceability gate passes and no pending registry entry is added
- [ ] #4 A faulted document: `IsFaulted` true, reads and `EncodeStateAsUpdate` work, mutators and `ApplyUpdate` throw `YInternalException`, `TryApplyUpdate` returns `Rejected`; the fault is permanent
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 `dotnet build YSharp.slnx` has 0 warnings
- [ ] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [ ] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [ ] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->
