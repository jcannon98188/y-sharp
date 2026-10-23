---
id: TASK-2.2
title: 'Spec 2: TestKit'
status: In Progress
assignee:
  - '@claude'
created_date: '2026-10-23 22:01'
updated_date: '2026-10-23 22:35'
labels: []
dependencies:
  - TASK-2.1
references:
  - docs/adr/0015-test-project-layout-tooling-and-ci.md
  - docs/adr/0011-fixture-format-and-generator.md
  - docs/adr/0016-ops-script-vocabulary-and-dump-formats.md
  - docs/adr/0009-allocation-and-memory-budgets.md
  - docs/adr/0017-randomized-test-port-plan.md
parent_task_id: TASK-2
ordinal: 29000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 0. The shared library every test project uses (ADR 0015). Owns the SIM-10+ harness entries.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Xoroshiro128plus, Xorshift32 seeding and prng draw helpers are bit-exact, checked against goldens generated from lib0
- [x] #2 Ops-script interpreter and tagged-value reader implement ADR 0016 against the stub
- [x] #3 Fixture loader fails on yjs/lib0 version mismatch; hex and structural diff assertions exist
- [x] #4 Allocation helper (GC.GetAllocatedBytesForCurrentThread, Release only, EventListener rerun on failure) and virtual TimeProvider exist
- [ ] #5 Node child-process client per ADR 0012 exists; port of the JS testHelper (TestConnector, init, compare) done with SIM-10+ entries covered
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Prng (Xorshift32, Xoroshiro128Plus, draw helpers) test-first against lib0 goldens.
2. Tagged values (ADR 0011) reader/writer/YValue conversion; hex + structural diff assertions.
3. Ops-script parser (strict) + interpreter core with step registry (ADR 0016) + standard steps.
4. Fixture loader with yjs/lib0/y-protocols header check.
5. AllocationAssert (Release, EventListener rerun) and VirtualTimeProvider.
6. NodeClient (ndjson, handshake, timeout restart).
7. Sim harness port (TestConnector, Init, Compare, ApplyRandomTests) with SIM-01..05 fixes.
8. Tier/Needs traits and env vars. Tests in tests/YSharp.Tests/TestKit.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
PRNG done: Xorshift32, Xoroshiro128Plus, Prng helpers; goldens from lib0 via tests/YSharp.TestKit/Goldens/generate-prng-goldens.mjs (10 seeds x 200 draws + 40-call helper script). Added xunit.v3.assert/extensibility.core to Directory.Packages.props (TestKit needs them). TestKit tests live in tests/YSharp.Tests/TestKit.

Validation: dotnet build YSharp.slnx (Debug+Release) 0 errors; dotnet test --solution YSharp.slnx: Debug 141 total/0 failed/10 skipped, Release 141/0/6 skipped. Skips are the 4 allocation tests in Debug (by design) plus tests that need a real implementation (YValue conversion, ops end-to-end, sim Init/Compare/ApplyRandomTests) which skip on the NotImplemented stub.
Decisions: tagged-value wire tags ({"$":"nan|inf|-inf|-0|undefined|bigint|bytes|str|obj"}) and fixture header shape ({schema,covers,versions{yjs,lib0,y-protocols},node,generator,seed}) and step field name 'step' are TestKit's choice because ADR 0011/0016 defer exact fields to tools/fixture-gen/schema/ops.schema.json (Spec 4); Spec 4 must align or change TestKit. Namespace YSharp.TestKit.Rng (avoids clash with class Prng). Sim harness TestKit sees public API only, so struct-store/pending/delete-set checks (SIM-02) go through ISimInternals supplied by YSharp.Tests. Tests carry Trait(Covers, SIM-1x) as placeholders until Spec 3's [Covers] exists; proposed IDs SIM-10 connector, SIM-11 init, SIM-12 compare, SIM-13 applyRandomTests, SIM-14 V2 framing. Added packages xunit.v3.assert and xunit.v3.extensibility.core to Directory.Packages.props; added echo-server.mjs copy item to YSharp.Tests.csproj. TieredCompilation=off for allocation tests is not yet set in YSharp.Tests (helper warms up instead); to be set by Spec 14.
<!-- SECTION:NOTES:END -->
