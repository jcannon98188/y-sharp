---
id: TASK-2.11
title: 'Spec 11: Hostile input and fuzz'
status: In Progress
assignee:
  - '@claude'
created_date: '2026-10-23 22:01'
updated_date: '2026-10-24 05:47'
labels: []
dependencies:
  - TASK-2.5
  - TASK-2.6
references:
  - docs/adr/0013-hostile-input-and-fuzz-strategy.md
  - docs/adr/0005-error-model-and-crash-safety.md
parent_task_id: TASK-2
ordinal: 38000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 3. Crash-safety proof: corpus, mutators, structure-aware builder, canary and guards.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Fixed corpus per HOS-* class replays on every commit
- [ ] #2 Byte mutator, structure-aware builder and ops-script FsCheck layers exist; time, allocation, stack and heap guards and the post-condition are enforced
- [ ] #3 Canary process and non-gating SharpFuzz harness set up
- [ ] #4 Traceability check passes; every owned registry entry is active or pending with a written reason and owner, and no new pending entries were added; owned ledger rows have an outcome other than pending; fixtures regenerate with no drift; gating tier within its time budget; traceability matrix attached to the task
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Fixture-gen: pass-through of per-case contract fields; add hostile-corpus scenarios (one fixture per HOS class, covers HOS-*, oracle outcome recorded) and regenerate.
2. YSharp.Fuzz.Tests infrastructure: guards (2 s worker timeout, 256 KB probe stack, k*N+1MB allocation bound, GCHeapHardLimit runtimeconfig), post-condition, tiers, candidate-file writer.
3. Generators: byte mutator, structure-aware V1/V2 builder (independent of y-sharp encoder, self-checked against oracle fixtures), ops-script delivery-schedule layer; FsCheck with fixed seeds and shrinking to shortest bytes then fewest structs.
4. Tests: fixed corpus replay per HOS class, truncation sweeps (updates V1/V2, SV, sync, awareness), limits boundary tests (default and tiny YLimits), ERR-* unit tests, Q15 Diverges.
5. Canary out-of-process host (HOS-DEEP doc depth, self-test with real stack overflow) and non-gating SharpFuzz harness plus workflow.
6. Registry/ledger: activate owned entries, ratchet pending mark, spec-11 matrix; build, test, fixture check; merge integration tip.
<!-- SECTION:PLAN:END -->
