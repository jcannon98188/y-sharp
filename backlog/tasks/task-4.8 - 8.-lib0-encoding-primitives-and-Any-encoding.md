---
id: TASK-4.8
title: 8. lib0 encoding primitives and Any encoding
status: Done
assignee: []
created_date: '2026-10-24 19:41'
updated_date: '2026-10-24 21:20'
labels:
  - spec
  - 'model:sonnet'
milestone: m-1
dependencies:
  - TASK-4.1
  - TASK-4.2
parent_task_id: TASK-4
ordinal: 8000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Every codec and the Any content need lib0's varints, strings and buffer reader and writer, ported bit-exact with rented buffers (ADR 0018) and strict EOF (ADR 0005).

**Features:** `Encoding`
**Owns:** `src/YSharp/Encoding/`
**Registry:** `Q` varint quirks (`quirks-varint.yaml`), Any-encoding `ENC` entries
**Test files:** Any and varint cases in `Codecs/ValueQuirkTests.cs`, `Codecs/UpdateLayoutTests.cs` (layout-only cases)
**Suggested model:** sonnet (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [x] #2 This spec's features are added to `Features.Landed`
- [x] #3 The traceability gate passes and no pending registry entry is added
- [x] #4 Readers reject truncation and over-long varints as Malformed without over-allocating; every rented buffer is returned on every exit path
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 `dotnet build YSharp.slnx` has 0 warnings
- [x] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [x] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [x] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->

## Comments

<!-- COMMENTS:BEGIN -->
author: @orchestrator
created: 2026-10-24 20:45
---
Note: implemented in namespace YSharp.Lib0 rather than YSharp.Encoding (ADR 0021) because YSharp.Encoding shadows System.Text.Encoding in existing TestKit/fuzz code. Later specs use YSharp.Lib0; ADR 0021 text may need updating.
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented on the impl/task-4 integration branch and merged; verified by the implementer with a 0-warning build, green Debug and Release test runs, clean fixture drift check and the traceability gate (see PR #1 and the task comments).
<!-- SECTION:FINAL_SUMMARY:END -->
