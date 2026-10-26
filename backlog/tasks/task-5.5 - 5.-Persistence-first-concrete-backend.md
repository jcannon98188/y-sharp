---
id: TASK-5.5
title: '5. Persistence: default in-memory backend and conformance suite'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-25 17:39'
updated_date: '2026-10-26 21:14'
labels:
  - server
  - hitl
  - 'model:sonnet'
dependencies:
  - TASK-5.4
parent_task_id: TASK-5
ordinal: 58000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
ADR 0024 SRV-12 (owner decision, 2026-10-26): YSharp.Server ships an in-memory store as the default backend and the storage interface for user-provided backends, but no file or database backend. The in-memory store from TASK-5.4 becomes a supported default, and a reusable conformance suite lets any user backend prove it meets the storage contract. **Suggested model:** sonnet (override freely).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The in-memory store is the server's default backend when the host registers none, and a user-provided IUpdateStore replaces it through one registration point
- [x] #2 Concurrent documents do not block each other on the in-memory store, and its operations are safe to call concurrently
- [x] #3 A public, reusable conformance suite covers crash at each step of append and compaction, partial writes, corrupted tail records, idempotent replay and concurrent documents, and the in-memory store passes it
- [x] #4 The contract for partial writes and corrupted tail records is defined in the interface documentation
- [x] #5 The documentation states that the in-memory default is not durable and loses all documents on restart
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. YServerOptions.Store: the one registration point; defaults to a new InMemoryUpdateStore; UsesDefaultStore flags the non-durable default.
2. IUpdateStore docs: settle the sequence contract for unacknowledged (failed or torn) appends, and the partial-write and corrupted-tail rules.
3. New package src/YSharp.Server.Testing with UpdateStoreConformance: framework-free, CreateAsync/RestartAsync plus optional crash hooks (torn append, corrupt record, interrupted compaction); InMemoryUpdateStoreConformance.
4. Tests: in-memory passes every check it can run; a framed test store with hooks passes all; a store with each contract break fails its check; defaults and registration; an open read blocks no append.
5. Inventory SRV-12; full suite.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Decisions: the conformance suite ships as its own package (YSharp.Server.Testing) with no test-framework dependency; a check passes when it returns, and results are Passed/Failed/NotSupported. Crashes at call boundaries are simulated by the suite (RestartAsync after each); crashes inside a call need backend hooks, and checks without a hook report NotSupported. The in-memory store reports NotSupported for the three hook checks, because its calls are atomic in memory and it has no torn or half-compacted states.
Contract change (IUpdateStore docs, within TASK-5.4's new API): a sequence number from an append that failed or was torn may be reused, since it was never acknowledged; acknowledged numbers are never reused while the log exists. A durable backend that derives its high-water mark from intact records on restart could not promise otherwise.
The torn-write and corruption checks are validated with a test-only framed store (tests/YSharp.Server.Tests/Storage/FramedTestStore.cs: length + checksum frames over simulated files, compaction by swap). It is not shipped (SRV-12: no file backend).
Sabotage: four defect variants of that store (skips corrupt records, drops later records on compaction, keeps the caller's buffer, reuses sequences after compaction) each fail the check written for them.
Suite changes (ADR 0022): new StoreConformanceTests and FramedTestStore in tests/YSharp.Server.Tests; the test project references YSharp.Server.Testing; SRV-12 in docs/inventory/server.yaml and id-lock.txt. No existing test changed.
Validation: YSharp.Server.Tests 73/73; full suite with YSHARP_RELEASE_GATE=1 (Debug) 4596 total, 0 failed, 776 skipped (Node tiers).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
YServerOptions.Store registers a backend and defaults to the non-durable InMemoryUpdateStore; IUpdateStore documents partial writes, corrupted tails and sequence reuse; the new YSharp.Server.Testing package ships a framework-free conformance suite with crash hooks. Verified: in-memory passes every check it can run, a framed hook store passes all 18, four broken stores each fail their check; full suite green with the release gate.
<!-- SECTION:FINAL_SUMMARY:END -->
