---
id: TASK-2.13
title: 'Spec 13: Live differential harness'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 22:01'
updated_date: '2026-10-24 10:37'
labels: []
dependencies:
  - TASK-2.4
  - TASK-2.5
  - TASK-2.6
  - TASK-2.7
references:
  - docs/adr/0012-live-differential-harness.md
  - docs/adr/0013-hostile-input-and-fuzz-strategy.md
parent_task_id: TASK-2
ordinal: 40000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 3. YSharp.Interop.Tests against a live Node child.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Stateless ops scripts over NDJSON with FsCheck generator, virtual clock, timeouts and oracle-misbehaved handling
- [x] #2 Smoke PR job and nightly slow tier run; failures replayable and saved as fixture candidates
- [x] #3 Protocol message catalogue, per-divergence normalisers and delivery-schedule steps specified and implemented
- [x] #4 Implements the 'applyForeign' step (ADR 0012) on the C# interpreter and the live Node client path, with schema change and [Covers]
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Node side: applyForeign step (tools/fixture-gen/lib/steps-foreign.mjs) + ops.schema.json entry + fixture scenario; serve.mjs live oracle server (hello with versions, run/ping ops, script-error vs oracle-error, fault ops behind YSHARP_DIFF_FAULTS)
2. C# side: applyForeign in TestKit StandardSteps (OpsContext.ForeignOutcomes); YSharp.Interop.Tests project: oracle assembly fixture, protocol catalogue, y-sharp observation builder, comparator (first-difference), normaliser registry, FsCheck script model/generator/shrinker (incl. delivery schedules), runner with oracle-misbehaved/hang handling, reporter (fixture candidate with repro header, YSHARP_REPLAY)
3. Tests: gating-safe self tests (generator, schema, comparator, reporter, normalisers) + Smoke/Nightly live tests (stateless ops, V1/V2, merge/diff/convert, convergence under schedules, applyForeign both directions, mutated-update outcome class, divergence normaliser)
4. Registry (differential.yaml DIFF-*), ADR 0012 message catalogue, workflows (require node, failing-seeds artifacts), traceability matrix, pending mark
5. Verify: dotnet test Debug+Release, npm run check, YSHARP_TIER=Smoke run; finalize
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implemented: tools/fixture-gen/serve.mjs + lib/server.mjs (live oracle), lib/steps-foreign.mjs + schema + fixtures/differential/apply-foreign.json (applyForeign), TestKit applyForeign, tests/YSharp.Interop.Tests (harness, FsCheck ScriptPlan generator, normalisers, reporting, 90 tests), DIFF-01..11 in docs/inventory/differential.yaml, ADR 0012 notes, smoke/nightly workflows (npm ci, YSHARP_REQUIRE_NODE=1). Verified: Debug and Release dotnet test --solution green (0 failed); YSHARP_TIER=Smoke run of Interop.Tests 76 passed + live properties skip on the API stub; npm run check clean; traceability gate 0 violations, 11 DIFF entries active. Live y-sharp-vs-Node properties could not run against real behaviour (stub); harness was validated with the oracle standing in for y-sharp (planted differences, shrinking, candidates, replay, hang/crash, oracle-misbehaved, wall-clock cap).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added the live differential harness: Node oracle server over NDJSON, applyForeign step on both interpreters (schema, fixture, [Covers DIFF-02]), YSharp.Interop.Tests with FsCheck script generator (delivery schedules, V1/V2, merge/diff/convert, mutated updates), virtual clock, timeouts and oracle-misbehaved handling, divergence normalisers, failure reports with fixture candidates and YSHARP_REPLAY, smoke/nightly tiers. Verified by Debug+Release solution test runs, Smoke-tier run with the oracle standing in for y-sharp, npm run check and the traceability gate.
<!-- SECTION:FINAL_SUMMARY:END -->
