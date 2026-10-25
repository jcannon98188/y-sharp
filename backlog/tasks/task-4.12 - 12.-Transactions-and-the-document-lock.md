---
id: TASK-4.12
title: 12. Transactions and the document lock
status: To Do
assignee: []
created_date: '2026-10-24 19:41'
updated_date: '2026-10-25 06:23'
labels:
  - spec
  - 'model:opus'
milestone: m-2
dependencies:
  - TASK-4.11
parent_task_id: TASK-4
ordinal: 12000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Every mutator runs in a transaction that takes the document lock (ADR 0006 CC-8), so the lock and the transaction lifecycle are core, not a later milestone.

**Features:** `Transactions`
**Owns:** `src/YSharp/Transactions/`, `Doc.cs` (transaction members), `Transaction`
**Registry:** lifecycle `CC` entries this spec can satisfy single-threaded (CC scenario and stress tests close in spec 39)
**Test files:** `Concurrency/LifecycleTests.cs`, `Concurrency/AcquireTests.cs` (uncontended cases)
**Seams provided:** `ScheduleSeam.Point`/`Block` calls from every lock transition; the partial hook points declared by spec 2 are invoked in cleanup
**Suggested model:** opus (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Every test whose required features have all landed passes, and no test requiring this spec's features skips
- [ ] #2 This spec's features are added to `Features.Landed`
- [ ] #3 The traceability gate passes and no pending registry entry is added
- [ ] #4 Implicit transactions join the calling thread's open transaction; `Transact`, `TryTransact`, `ReadTransact`, `BeginTransaction`, `TransactionScope` and `TransactWithTimeout` follow ADR 0006 and 0008
- [ ] #5 Stale views throw `YLifetimeException`; read-to-write upgrade returns `UpgradeRefused` or throws `YConcurrencyException`
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
created: 2026-10-25 06:20
---
Owner decision needed (ADR 0022 gate changes; 5 tests red until answered): (1) add [Requires(Feature.DocLifecycle)] to the CC-17 Destroy tests in LifecycleTests (3 failing + ADestroyedDocument_KeepsAcceptingAcquires_OnlyIsDestroyedChanges); (2) HotPathBudgetTests.ReadTransact_WithStaticLambda_AllocatesNothing -> [Requires(Text, Array, Map)]; (3) HotPathBudgetTests.EncodeStateAsUpdate_IntoPreSizedWriter_AllocatesNothing -> [Requires(V1, Text, Array, Map)]; (4) confirm TestKit ScheduleSeam TypeName change to YSharp.Transactions.ScheduleSeam. For spec 39: LockModelTests.TryTransact_WhileTheCallingThreadHoldsAReadTransaction_ReturnsUpgradeRefusedAtOnce checks from another thread, which should get Contended; run the check on the reading thread. Also: cleanup now calls HookGarbageCollection before HookSubdocs (Yjs order).
---

author: @orchestrator
created: 2026-10-25 06:23
---
Owner decision needed: pooled Transaction objects make CC-9 (stale transaction throws) and CC-7/BUD-01 (zero-allocation acquire) incompatible while Transaction is a class. Options: A) allocate a small per-transaction object (~24-40 B) over pooled state, raising BUD-01/02 from 0; B) make Transaction a readonly struct handle with a generation (public API change, breaks 13 suite uses and Assert.Same checks); C) amend CC-9 to hold only until reuse. Reuse test is on branch task-4.12-reuse-test (9f47d27).
---
<!-- COMMENTS:END -->
