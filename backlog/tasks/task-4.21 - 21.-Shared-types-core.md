---
id: TASK-4.21
title: 21. Shared types core
status: In Progress
assignee:
  - '@claude'
created_date: '2026-10-24 19:42'
updated_date: '2026-10-25 20:01'
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

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. YType core: Branch.Owner/TypeRef/TypeName, Doc/Parent/IsIntegrated, prelim buffer (T-0.4/T-0.5), attach replays buffered content in the integrating transaction (T-0.6/T-0.8).
2. YEntry content mapping: Any/Binary/Type/Doc to Content*; reject an already-integrated type (T-0.13, Q10).
3. List core (insert/delete/get/iterate over Branch items) with search markers; map core (set/get/delete/has, last-writer by parent sub).
4. Doc.GetArray/GetMap/GetText/GetXml*/Get placeholder upgraded in place; Share snapshot; Doc.ToJson.
5. YUntyped Clone/ToJson.
6. Land Feature.TypesCore; run suites; gate corrections under the standing approval, listed in notes.
7. Debug + Release tests, fixture-gen check, api-surface check, memory gate.
Commit after each step (session-limit safety).
<!-- SECTION:PLAN:END -->
