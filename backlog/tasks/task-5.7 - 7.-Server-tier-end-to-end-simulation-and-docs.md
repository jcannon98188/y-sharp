---
id: TASK-5.7
title: 7. Server-tier end-to-end simulation and docs
status: Done
assignee:
  - '@claude'
created_date: '2026-10-25 17:39'
updated_date: '2026-10-26 21:41'
labels:
  - server
  - 'model:sonnet'
dependencies:
  - TASK-5.5
  - TASK-5.6
  - TASK-5.3
parent_task_id: TASK-5
ordinal: 60000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Ties the relay tier, persistence and mailbox together and shows that real peers converge through them. Reuses the existing simulation harness. **Suggested model:** sonnet (override freely).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A simulation of N Yjs-protocol peers through the server tier with restarts in the middle converges to the same state as the Doc-only path
- [x] #2 The existing YSharp.Tests, Fuzz and Interop suites pass unchanged
- [x] #3 Docs describe the layering, the storage interface and how to host the server tier
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
ServerSimulationTests: 12 seeded runs (every third with a validation hook, so the hot Doc is in the path) of 4 y-websocket peers through YServer for 250 steps: online and offline edits to a text (inserts, deletes, formats), an array and a map; flushes; drops and reconnects; server restarts over the same store (one forced mid-run); compaction both while the document is open and between restarts. Every peer, and a fresh peer loading from the store after a final restart, ends byte-equal to a Doc that applied every local edit directly.

The edits reuse the relay tests' workload (RelayWorkload.Edit, extracted without changing its draws), without surrogate pairs. Found while building it: a format that changes nothing, starting inside a surrogate pair, splits the pair into two U+FFFD in that replica only and emits no update, so that replica diverges for good. Real Yjs does the same, byte for byte (checked against the pinned oracle; yjs#248 behaviour), so it is faithful and not a server bug; it is documented on RelayWorkload and the simulation.

Docs: docs/server.md (layering and pipeline, backpressure, the storage contract and conformance suite, an ASP.NET Core hosting sample, validation hooks, testing).

Suite changes:
- tests/YSharp.TestKit/Relay/RelayWorkload.cs: Edit(rng, doc, astral) extracted (same draws for Updates)
- tests/YSharp.Server.Tests/Simulation/ServerSimulationTests.cs (new; SRV-4, 9, 11, 19)

Full suite with YSHARP_RELEASE_GATE=1: 0 failed (3866 passed, 776 skipped); YSharp.Tests, Fuzz and Interop unchanged.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Server-tier end-to-end simulation (peers, drops, offline edits, restarts, live and offline compaction, with and without a hook) converging to the Doc-only path, plus docs/server.md on layering, storage and hosting. Found that a no-op format inside a surrogate pair corrupts only the local replica, faithfully to Yjs.
<!-- SECTION:FINAL_SUMMARY:END -->
