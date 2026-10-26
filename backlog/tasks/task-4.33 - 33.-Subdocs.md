---
id: TASK-4.33
title: 33. Subdocs
status: To Do
assignee: []
created_date: '2026-10-24 19:42'
updated_date: '2026-10-26 05:56'
labels:
  - spec
  - 'model:sonnet'
milestone: m-5
dependencies:
  - TASK-4.29
parent_task_id: TASK-4
ordinal: 33000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Subdocument tracking, load and the `SubdocsChanged` event, through the subdocs hook.

**Features:** `Subdocs`
**Owns:** `src/YSharp/DocFeatures/Subdocs/`, `Doc.Subdocs.cs`
**Registry:** `SUB` entries
**Test files:** `DocFeatures/SubdocTests.cs`
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
From TASK-4.28: DiffRun.ReachesUnlandedSubdocs discards mutated-update differential cases that reach the ContentDoc sketch (TypeList.ContentAt) while Subdocs is not landed. When this spec lands Subdocs, delete that check and UnlandedSubdocsDiscardTests' else branch, and rerun the Nightly differential.
<!-- SECTION:NOTES:END -->
