---
id: TASK-4.11
title: 11. Store
status: To Do
assignee: []
created_date: '2026-10-24 19:41'
updated_date: '2026-10-24 21:43'
labels:
  - spec
  - 'model:opus'
milestone: m-2
dependencies:
  - TASK-4.6
  - TASK-4.9
parent_task_id: TASK-4
ordinal: 11000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Port the storage model chosen by the M0 storage ADR into `Store/` behind the opaque `ItemRef` handle (ADR 0021), and hold it to the spike numbers.

**Features:** `Store`
**Owns:** `src/YSharp/Store/`, the real-store driver adapter in `tests/YSharp.Tests/Allocation/`
**Registry:** store-level `STR` entries (struct store, contiguity, find and split)
**Test files:** `Structs/StructStoreTests.cs` (store cases)
**Suggested model:** opus (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [ ] #2 This spec's features are added to `Features.Landed`
- [ ] #3 The traceability gate passes and no pending registry entry is added
- [ ] #4 The spike harness passes on the real store through a third driver adapter at or below the spike's figures
- [ ] #5 `spike/` is deleted
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 `dotnet build YSharp.slnx` has 0 warnings
- [ ] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [ ] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [ ] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->

## Comments

<!-- COMMENTS:BEGIN -->
author: @orchestrator
created: 2026-10-24 21:43
---
Owner decision needed: StructStoreTests.LookupOfAnUnknownClient_IsHeldAsPending_InsteadOfFailing is gated only on Store but calls Doc.TryApplyUpdate/EncodeStateAsUpdate/GetStateVector, so it now fails with NotImplementedException. Recommend adding [Requires(Feature.V1)] (spec 14). Also confirm: SpikeHarnessTests moved to [Requires(Store)] and two replaced tests; RealStoreDriverTests replaces SpikeObjectPerItemTests. Notes: search markers live in Store/ (ADR 0021 gives them to Types/, spec 21); ADR 0023 'How to rerun' still points at the deleted spike.
---
<!-- COMMENTS:END -->
