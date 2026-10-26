---
id: TASK-4.3
title: '3. Spike harness, driver and shared content layer'
status: Done
assignee: []
created_date: '2026-10-24 19:41'
updated_date: '2026-10-24 21:20'
labels:
  - spec
  - 'model:haiku'
milestone: m-0
dependencies:
  - TASK-4.2
parent_task_id: TASK-4
ordinal: 3000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
ADR 0018's spike amendment needs a driver interface, a harness that reproduces `MemoryWorkloads` and `SteadyStateEdits`, and one content layer (text-buffer slices, 8-byte value columns, map payloads) shared by both candidates. M2 and M3 later reuse the same harness on the real store.

**Owns:** `tests/YSharp.Tests/Allocation/` harness and driver files, the content layer in `spike/YSharp.StoreSpike`
**Suggested model:** haiku (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Driver interface covers insert char at index, delete at index, push value, set key, commit
- [x] #2 Harness runs the three slow-tier workloads and the steady-state mix exactly as `MemoryWorkloads` defines them, one process per workload, median of 3, Release, TieredCompilation off, workstation GC
- [x] #3 Harness reports retained bytes per operation, structs per operation, gen2 count, LOH growth and throughput
- [x] #4 Shared content layer keeps every chunk at or below 64 KB and is used through the driver by both candidates
- [x] #5 The traceability gate passes and no pending registry entry is added
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
