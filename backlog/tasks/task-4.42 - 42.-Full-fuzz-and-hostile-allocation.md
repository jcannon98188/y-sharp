---
id: TASK-4.42
title: 42. Full fuzz and hostile allocation
status: To Do
assignee: []
created_date: '2026-10-24 19:42'
updated_date: '2026-10-25 23:32'
labels:
  - spec
  - 'model:sonnet'
milestone: m-7
dependencies:
  - TASK-4.28
  - TASK-4.37
parent_task_id: TASK-4
ordinal: 42000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Every decoder exists, so the full hostile corpus, canary and SharpFuzz job can run and BUD-12 can be fixed.

**Features:** `Fuzz`
**Owns:** `tests/YSharp.Fuzz.Tests`, `tests/YSharp.Fuzz.Canary`, `tools/sharpfuzz`
**Registry:** `HOS`, remaining `ERR` entries
**Test files:** `YSharp.Fuzz.Tests/*`, `Allocation/HostileAllocationTests.cs`
**Budgets:** BUD-12 (hostile input k*N plus constant): constants recorded, active
**Handoff section-7 items:** Record the BUD-12 constants (the tests currently print the value and skip).
**Suggested model:** sonnet (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [ ] #2 This spec's features are added to `Features.Landed`
- [ ] #3 The traceability gate passes and no pending registry entry is added
- [ ] #4 No input in the corpus or generators faults a document; the post-condition holds everywhere
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
From TASK-4.24 (Text landing ungated these; gated on Fuzz until this spec). Release measurements against the AmplificationTests bounds:
- 5000 clients with one struct each: 4.7 MB, bound 4.24 MB.
- 5000 structs waiting on missing origins: 11.5 MB, bound 3.29 MB. Decode is about 2.45 MB; the pending dry-run path dominates.
- ThousandsOfTinyPendingUpdates, when the dependency arrives: 5.2 MB, bound 4.1 MB.
A StructRef is 152 bytes. The 3000-update merge was cut from 5.17 MB to 1.56 MB in TASK-4.24 (lazy UpdateRefs clients, exact read-order sizing). Corpus_References is also gated here: the hos-ref corpus expects ok or Rejected for parent-is-self and forward-reference-chain-200, where spec 14 decided Malformed (ENC-49); reconcile the corpus.
<!-- SECTION:NOTES:END -->
