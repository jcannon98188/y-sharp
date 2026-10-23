---
id: TASK-2.2
title: 'Spec 2: TestKit'
status: To Do
assignee: []
created_date: '2026-10-23 22:01'
updated_date: '2026-10-23 22:26'
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
- [ ] #1 Xoroshiro128plus, Xorshift32 seeding and prng draw helpers are bit-exact, checked against goldens generated from lib0
- [ ] #2 Ops-script interpreter and tagged-value reader implement ADR 0016 against the stub
- [ ] #3 Fixture loader fails on yjs/lib0 version mismatch; hex and structural diff assertions exist
- [ ] #4 Allocation helper (GC.GetAllocatedBytesForCurrentThread, Release only, EventListener rerun on failure) and virtual TimeProvider exist
- [ ] #5 Node child-process client per ADR 0012 exists; port of the JS testHelper (TestConnector, init, compare) done with SIM-10+ entries covered
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
PRNG done: Xorshift32, Xoroshiro128Plus, Prng helpers; goldens from lib0 via tests/YSharp.TestKit/Goldens/generate-prng-goldens.mjs (10 seeds x 200 draws + 40-call helper script). Added xunit.v3.assert/extensibility.core to Directory.Packages.props (TestKit needs them). TestKit tests live in tests/YSharp.Tests/TestKit.
<!-- SECTION:NOTES:END -->
