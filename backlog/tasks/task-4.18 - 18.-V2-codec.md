---
id: TASK-4.18
title: 18. V2 codec
status: To Do
assignee: []
created_date: '2026-10-24 19:41'
updated_date: '2026-10-24 19:42'
labels:
  - spec
  - 'model:sonnet'
milestone: m-3
dependencies:
  - TASK-4.17
parent_task_id: TASK-4
ordinal: 18000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The V2 encoder and decoder adapters behind the codec seam, the `UpdateV2` event, and limits on every V2 column (ADR 0005).

**Features:** `V2`
**Owns:** `src/YSharp/Codecs/V2/`
**Registry:** V2 `ENC` entries, V2 decode `ERR` and `HOS` entries
**Test files:** V2 cases of `Codecs/MalformedInputTests.cs` and the structure files, `YSharp.Fuzz.Tests` V2 corpus
**Handoff section-7 items:** Fix (with evidence, ADR 0022) the hand-built V2 negative-clock bytes in `Q22_ANegativeClockFromAV2DiffColumn_IsMalformed`; the expected Malformed outcome stays.
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
