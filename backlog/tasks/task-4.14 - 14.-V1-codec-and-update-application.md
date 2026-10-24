---
id: TASK-4.14
title: 14. V1 codec and update application
status: To Do
assignee: []
created_date: '2026-10-24 19:41'
updated_date: '2026-10-24 19:42'
labels:
  - spec
  - 'model:opus'
milestone: m-2
dependencies:
  - TASK-4.13
parent_task_id: TASK-4
ordinal: 14000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The V1 wire format through the generic codec seam (ADR 0021): the decoder and encoder adapters, `ApplyUpdate`/`TryApplyUpdate`, `EncodeStateAsUpdate` and state vectors.

**Features:** `V1`
**Owns:** `src/YSharp/Codecs/` (interface and V1 adapter), `Doc.cs` (apply and encode members)
**Registry:** V1 `ENC` entries, `Q` client-id quirks (`quirks-clients.yaml`), V1 decode `ERR` and `HOS` entries
**Test files:** `Codecs/UpdateLayoutTests.cs`, `Codecs/MalformedInputTests.cs` (V1), `Codecs/UpstreamPortTests.cs`, `Structs/UpdateApplicationTests.cs`, `Structs/StructsFixtureTests.cs`, `YSharp.Fuzz.Tests` V1 corpus and canary cases
**Seams provided:** `Doc.DumpStructs()`, `Doc.HasPending`
**Suggested model:** opus (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [ ] #2 This spec's features are added to `Features.Landed`
- [ ] #3 The traceability gate passes and no pending registry entry is added
- [ ] #4 Decode is atomic: a decode error changes nothing and returns every rented buffer; all `YLimits` are checked before allocation
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 `dotnet build YSharp.slnx` has 0 warnings
- [ ] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [ ] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [ ] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->
