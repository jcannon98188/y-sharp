---
id: TASK-4.6
title: 6. Spike selection and storage ADR
status: Done
assignee: []
created_date: '2026-10-24 19:41'
updated_date: '2026-10-24 21:20'
labels:
  - spec
  - 'model:opus'
milestone: m-0
dependencies:
  - TASK-4.4
  - TASK-4.5
parent_task_id: TASK-4
ordinal: 6000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Measure both candidates, apply the ADR 0018 selection rules and record the storage model in a follow-up ADR. Nothing in M2 builds on a store that fails. HITL only if a rule escalates (winner over 2x slower, or both fail).

**Owns:** the new storage ADR in `docs/adr/`, deletion of the losing candidate
**Suggested model:** opus (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Both candidates measured with the spike harness; results table includes bytes and structs per operation, gen2, LOH growth, throughput, runtime, GC config and commit
- [x] #2 Winner chosen by the ADR 0018 selection rules, naming the rule that decided; owner sign-off obtained if the winner is over 2x slower, work stopped and escalated if both fail
- [x] #3 Storage ADR records the table, the choice, the ItemRef fit, the index-reuse policy (arenas), the payload release rule, remaining risks and how to rerun
- [x] #4 Losing candidate deleted
- [x] #5 The traceability gate passes and no pending registry entry is added
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
created: 2026-10-24 21:10
---
Owner decision: fix candidate A's push path to use the search marker (Yjs typeListPushGenerics), remeasure both candidates, then select. Gen2 gate covers only the steady-state check; the 20% headroom rule cannot hold for either candidate (shared value column ~8.07 B/op).
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented on the impl/task-4 integration branch and merged; verified by the implementer with a 0-warning build, green Debug and Release test runs, clean fixture drift check and the traceability gate (see PR #1 and the task comments).
<!-- SECTION:FINAL_SUMMARY:END -->
