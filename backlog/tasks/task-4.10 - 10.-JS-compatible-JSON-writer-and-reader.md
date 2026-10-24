---
id: TASK-4.10
title: 10. JS-compatible JSON writer and reader
status: Done
assignee: []
created_date: '2026-10-24 19:41'
updated_date: '2026-10-24 21:20'
labels:
  - spec
  - 'model:sonnet'
milestone: m-1
dependencies:
  - TASK-4.9
parent_task_id: TASK-4
ordinal: 10000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Byte-identical JSON with JS for `ToJson`, relative positions and awareness (docs/research/json.md, ADR 0007).

**Features:** `Json`
**Owns:** `src/YSharp/Values/Json/`
**Registry:** `Q` string and number quirks (`quirks-strings.yaml`, JSON entries)
**Test files:** `Codecs/JsonTests.cs`, `Codecs/StringQuirkTests.cs` (JSON cases)
**Suggested model:** sonnet (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [x] #2 This spec's features are added to `Features.Landed`
- [x] #3 The traceability gate passes and no pending registry entry is added
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 `dotnet build YSharp.slnx` has 0 warnings
- [x] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [x] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [x] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented on the impl/task-4 integration branch and merged; verified by the implementer with a 0-warning build, green Debug and Release test runs, clean fixture drift check and the traceability gate (see PR #1 and the task comments).
<!-- SECTION:FINAL_SUMMARY:END -->
