---
id: TASK-4.2
title: 2. Source layout skeleton
status: To Do
assignee: []
created_date: '2026-10-24 19:41'
updated_date: '2026-10-24 19:55'
labels:
  - spec
  - 'model:haiku'
milestone: m-0
dependencies: []
parent_task_id: TASK-4
ordinal: 2000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
ADR 0021 splits `src/YSharp` into module folders with one owning spec per file. The split touches every stub file, so it happens once, before anything fans out.

**Owns:** `src/YSharp/**` (the move only)
**Suggested model:** haiku (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The five stub files are split into the ADR 0021 module folders, with public types that span owners as partial classes (one file per owning module); public types stay in `YSharp`, internals in `YSharp.<Module>`
- [ ] #2 The M2 lifecycle hook points (before observers, observers, after transaction, subdocs, GC) are declared as `partial` methods on the transaction cleanup path
- [ ] #3 Every member still throws `NotImplementedException`; an API-diff check (public surface before and after) shows no change and is committed as a reusable script
- [ ] #4 Build has 0 warnings and the suite result is unchanged
- [ ] #5 The traceability gate passes and no pending registry entry is added
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
created: 2026-10-24 19:55
---
Owner decision needed: ScheduleSeam stays in namespace YSharp, not YSharp.Transactions (ADR 0021), because tests/YSharp.Tests/Concurrency/ConcSupport.cs resolves it through the enclosing namespace; moving it needs a test-file change owned by TASK-4.12. Recommend moving it when TASK-4.12 lands.
---
<!-- COMMENTS:END -->
