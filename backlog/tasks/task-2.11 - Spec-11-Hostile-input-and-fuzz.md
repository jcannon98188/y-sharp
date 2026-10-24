---
id: TASK-2.11
title: 'Spec 11: Hostile input and fuzz'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 22:01'
updated_date: '2026-10-24 06:10'
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
- [x] #1 Fixed corpus per HOS-* class replays on every commit
- [x] #2 Byte mutator, structure-aware builder and ops-script FsCheck layers exist; time, allocation, stack and heap guards and the post-condition are enforced
- [x] #3 Canary process and non-gating SharpFuzz harness set up
- [x] #4 Traceability check passes; every owned registry entry is active or pending with a written reason and owner, and no new pending entries were added; owned ledger rows have an outcome other than pending; fixtures regenerate with no drift; gating tier within its time budget; traceability matrix attached to the task
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Validation: dotnet build YSharp.slnx clean (0 warnings); dotnet test --solution YSharp.slnx Debug: 1244 total, 0 failed, 227 passed, 1017 skipped on the API stub (whole run ~5 s); fixture-gen npm run check: fixtures up to date (124 files); traceability gate: 0 violations, 42 pending entries (mark ratcheted 72 -> 42), matrix in docs/inventory/matrices/spec-11-traceability.md. Release run has one pre-existing flaky test (AllocationAssertTests.AtMost_AllocatingOperation_FailsAndNamesTheAllocatingType, fails ~1 in 3 on YSharp.Tests alone). Deviations: ERR-20 stays pending (no fault-injection seam on the stub). Assumed limit semantics documented in LimitsTests (MaxAnyDepth counts nested containers). Canary stack-overflow self-test and guard self-tests run without an implementation. AFL++ run of the SharpFuzz job is untested (Windows host); harness builds and --export-seeds works.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added the hostile corpus (8 fixtures in fixtures/hostile via fixture-gen, oracle outcomes recorded), the YSharp.Fuzz.Tests harness (guards: 2 s timeout, 256 KB probe stack, 64*N+1 MB allocation bound, 400 MB heap limit, post-condition), three FsCheck layers (byte mutator, structure-aware V1/V2 builder checked byte for byte against oracle fixtures, ops-script with delivery-schedule mutation), truncation sweeps, limit boundaries under default and tiny YLimits, ERR-01..21 tests, Q15 Diverges test, out-of-process canary (1M-deep documents; self-test proves a real stack overflow is reported by name) and the non-gating SharpFuzz harness plus sharpfuzz.yml. 30 of 31 owned registry entries are active; ERR-20 stays pending with a written reason. Verified by full build/test, fixture drift check and the traceability gate (0 violations).
<!-- SECTION:FINAL_SUMMARY:END -->
