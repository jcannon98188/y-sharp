---
id: TASK-4.9
title: '9. YValue, StateVector, ids, errors and limits'
status: To Do
assignee: []
created_date: '2026-10-24 19:41'
updated_date: '2026-10-24 19:42'
labels:
  - spec
  - 'model:sonnet'
milestone: m-1
dependencies:
  - TASK-4.8
parent_task_id: TASK-4
ordinal: 9000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The value layer every other area uses (ADR 0007, ADR 0008), plus the error family and `YLimits` (ADR 0005).

**Features:** `Values`
**Owns:** `src/YSharp/Values/` (except JSON)
**Registry:** `V`, `Q` value quirks (`quirks-values.yaml`), value-level `ERR` entries
**Test files:** `Codecs/ValueQuirkTests.cs`, `TestKit/TaggedValueTests.cs`, `TestKit/TierTests.cs`, `ScaffoldSmokeTests.cs`
**Budgets:** BUD-03 (scalar `YValue` reads and `AttributeEquals`, zero) recorded at M1 close by this spec
**Suggested model:** sonnet (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [ ] #2 This spec's features are added to `Features.Landed`
- [ ] #3 The traceability gate passes and no pending registry entry is added
- [ ] #4 BUD-03 reads 0 bytes and its entry is active
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 `dotnet build YSharp.slnx` has 0 warnings
- [ ] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [ ] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [ ] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->
