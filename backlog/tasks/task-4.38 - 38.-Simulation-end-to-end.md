---
id: TASK-4.38
title: 38. Simulation end to end
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
  - TASK-4.37
parent_task_id: TASK-4
ordinal: 38000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The sim harness has never run against an implementation. This absorbs TASK-2.2's open criterion and calibrates the gating budgets.

**Features:** `Sim`
**Owns:** `tests/YSharp.Tests/Simulation/`, `ISimInternals` adapter, `SimCase.GatingSeeds`
**Registry:** `SIM` entries
**Test files:** `Simulation/*`, `TestKit/SimHarnessTests.cs`
**Seams provided:** `ISimInternals` (over `HasPending` and `DumpStructs`)
**Handoff section-7 items:** End-to-end sim harness (`Init`, `Compare`, `ApplyRandomTests`) via `ISimInternals` (from TASK-2.2 AC #5); calibrate the 2-minute gating time budget and the ~60 s randomized budget via `SimCase.GatingSeeds` (with evidence, ADR 0022).
**Suggested model:** opus (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [ ] #2 This spec's features are added to `Features.Landed`
- [ ] #3 The traceability gate passes and no pending registry entry is added
- [ ] #4 Port of the JS testHelper (TestConnector, init, compare) runs end to end with SIM-10+ entries covered
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 `dotnet build YSharp.slnx` has 0 warnings
- [ ] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [ ] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [ ] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->
